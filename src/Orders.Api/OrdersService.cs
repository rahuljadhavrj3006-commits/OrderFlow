using OrderFlow.Shared;

namespace Orders.Api;

public sealed class OrdersService(
    IInventoryClient inventory,
    IOrderRepository repository,
    IEventPublisher events,
    IdempotencyStore idempotency,
    ILogger<OrdersService> logger)
{
    public async Task<Order> CreateAsync(string? idempotencyKey, CreateOrderRequest request, CancellationToken ct)
    {
        Validate(request);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return await CreateCoreAsync(request, ct);
        }

        // The shared work deliberately ignores the first caller's token: if that client disconnects
        // we still want the saga (reserve -> save -> publish) to finish or compensate cleanly.
        return await idempotency.ExecuteAsync(
            idempotencyKey.Trim(),
            Fingerprint(request),
            () => CreateCoreAsync(request, CancellationToken.None),
            ct);
    }

    private async Task<Order> CreateCoreAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var orderId = Guid.NewGuid();
        var items = request.Items!.Select(i => new OrderItem(i.Sku.Trim(), i.Quantity)).ToList();

        try
        {
            // Reserve every line in PARALLEL: total latency ~ the slowest call, not the sum of all calls.
            // Task.WhenAll only completes when ALL tasks finished, so on failure nothing is still in flight.
            var results = await Task.WhenAll(items.Select(i => inventory.ReserveAsync(i.Sku, i.Quantity, orderId, ct)));

            var failures = items.Zip(results)
                .Where(pair => !pair.Second.Success)
                .Select(pair => $"{pair.First.Sku}: {pair.Second.Reason}")
                .ToList();

            if (failures.Count > 0)
            {
                throw new ConflictException("Could not reserve stock - " + string.Join("; ", failures));
            }
        }
        catch
        {
            // Saga compensation: undo any partial reservations.
            await SafeReleaseAsync(orderId);
            throw;
        }

        var order = new Order(orderId, request.CustomerId!.Trim(), items, OrderStatus.Confirmed, DateTimeOffset.UtcNow, Guid.NewGuid());
        await repository.AddAsync(order);
        await events.PublishAsync(new OrderEvent(orderId, OrderEventType.Confirmed, order.CreatedAt), ct);

        logger.LogInformation("Order {OrderId} confirmed with {LineCount} line(s)", orderId, items.Count);
        return order;
    }

    public async Task<Order> GetAsync(Guid id, CancellationToken ct)
    =>
        await repository.GetAsync(id, ct) ?? throw new NotFoundException($"Order {id} was not found.");

    public async Task<PagedResult<Order>> QueryAsync(OrderStatus? status, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) throw new DomainValidationException("page must be >= 1.");
        if (pageSize is < 1 or > 100) throw new DomainValidationException("pageSize must be between 1 and 100.");
        return await repository.QueryAsync(status, page, pageSize, ct);
    }

    /// <summary>Idempotent cancel: cancelling twice is not an error. Uses optimistic concurrency instead of a lock.</summary>
    public async Task<Order> CancelAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            var order = await GetAsync(id, ct);
            if (order.Status == OrderStatus.Cancelled)
            {
                return order;
            }

            var cancelled = order with { Status = OrderStatus.Cancelled, Version = Guid.NewGuid() };
            if (!await repository.TryUpdateAsync(cancelled, order, ct))
            {
                continue; // someone changed it between our read and write: re-read and retry
            }

            await SafeReleaseAsync(id);
            await events.PublishAsync(new OrderEvent(id, OrderEventType.Cancelled, DateTimeOffset.UtcNow), ct);
            return cancelled;
        }
    }

    private async Task SafeReleaseAsync(Guid orderId)
    {
        try
        {
            // CancellationToken.None on purpose: cleanup must run even if the request that triggered it was cancelled.
            await inventory.ReleaseAsync(orderId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Compensation can fail too. In production: persist a "release pending" record and reconcile (outbox pattern).
            logger.LogError(ex, "Failed to release stock for order {OrderId}; manual reconciliation needed", orderId);
        }
    }

    private static void Validate(CreateOrderRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            errors.Add("customerId is required.");
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            errors.Add("At least one item is required.");
        }
        else if (request.Items.Any(i => i is null))
        {
            errors.Add("Items must not contain null entries.");
        }
        else
        {
            if (request.Items.Count > 20)
            {
                errors.Add("An order can contain at most 20 distinct items.");
            }

            foreach (var item in request.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Sku))
                {
                    errors.Add("sku is required for every item.");
                }

                if (item.Quantity is < 1 or > 100)
                {
                    errors.Add($"quantity for '{item.Sku}' must be between 1 and 100.");
                }
            }

            var hasDuplicates = request.Items
                .GroupBy(i => (i.Sku ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() > 1);
            if (hasDuplicates)
            {
                errors.Add("Duplicate SKUs are not allowed; combine the quantities.");
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(string.Join(" ", errors));
        }
    }

    /// <summary>Order-independent summary of the request, used to detect Idempotency-Key reuse with a different body.</summary>
    private static string Fingerprint(CreateOrderRequest request) =>
        request.CustomerId!.Trim() + "|" + string.Join(",",
            request.Items!
                .OrderBy(i => i.Sku.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(i => $"{i.Sku.Trim().ToUpperInvariant()}x{i.Quantity}"));
}
