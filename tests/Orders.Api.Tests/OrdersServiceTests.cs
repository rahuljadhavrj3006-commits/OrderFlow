using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Shared;
using Orders.Api;

namespace Orders.Api.Tests;

public class OrdersServiceTests
{
    private sealed class FakeInventory : IInventoryClient
    {
        public int ReserveCalls;
        public int ReleaseCalls;
        public HashSet<string> OutOfStock { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task<ReserveResult> ReserveAsync(string sku, int quantity, Guid orderId, CancellationToken ct)
        {
            Interlocked.Increment(ref ReserveCalls);
            await Task.Delay(50, ct); // simulate network latency so concurrent callers really overlap
            return OutOfStock.Contains(sku)
                ? new ReserveResult(false, $"no stock for {sku}")
                : new ReserveResult(true, null);
        }

        public Task ReleaseAsync(Guid orderId, CancellationToken ct)
        {
            Interlocked.Increment(ref ReleaseCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePublisher : IEventPublisher
    {
        private readonly List<OrderEvent> _events = new();
        public IReadOnlyList<OrderEvent> Events { get { lock (_events) return _events.ToList(); } }

        public ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct = default)
        {
            lock (_events) _events.Add(orderEvent);
            return ValueTask.CompletedTask;
        }
    }

    private static (OrdersService Service, FakeInventory Inventory, FakePublisher Publisher, InMemoryOrderRepository Repo) Create()
    {
        var inventory = new FakeInventory();
        var publisher = new FakePublisher();
        var repo = new InMemoryOrderRepository();
        var service = new OrdersService(inventory, repo, publisher, new IdempotencyStore(), NullLogger<OrdersService>.Instance);
        return (service, inventory, publisher, repo);
    }

    private static CreateOrderRequest Request(params (string Sku, int Qty)[] lines) =>
        new("customer-1", lines.Select(l => new OrderItem(l.Sku, l.Qty)).ToList());

    [Fact]
    public async Task Creates_order_and_publishes_event()
    {
        var (service, inventory, publisher, repo) = Create();

        var order = await service.CreateAsync(null, Request(("A", 1), ("B", 2)), CancellationToken.None);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(2, inventory.ReserveCalls);
        Assert.NotNull(await repo.GetAsync(order.Id));
        Assert.Single(publisher.Events);
    }

    [Fact]
    public async Task Out_of_stock_line_rejects_order_and_releases_reservations()
    {
        var (service, inventory, publisher, repo) = Create();
        inventory.OutOfStock.Add("B");

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(null, Request(("A", 1), ("B", 1)), CancellationToken.None));

        Assert.Equal(1, inventory.ReleaseCalls);                       // compensation ran
        Assert.Equal(0, (await repo.QueryAsync(null, 1, 10)).TotalCount);           // nothing persisted
        Assert.Empty(publisher.Events);                                // nothing announced
    }

    [Fact]
    public async Task Concurrent_requests_with_same_idempotency_key_create_exactly_one_order()
    {
        var (service, inventory, publisher, repo) = Create();
        var request = Request(("A", 1));

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => service.CreateAsync("key-1", request, CancellationToken.None))));

        Assert.Single(results.Select(o => o.Id).Distinct());
        Assert.Equal(1, inventory.ReserveCalls);
        Assert.Equal(1, (await repo.QueryAsync(null, 1, 10)).TotalCount);
        Assert.Single(publisher.Events);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_payload_is_a_conflict()
    {
        var (service, _, _, _) = Create();
        await service.CreateAsync("key-2", Request(("A", 1)), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync("key-2", Request(("A", 5)), CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_attempt_does_not_poison_the_idempotency_key()
    {
        var (service, inventory, _, _) = Create();
        inventory.OutOfStock.Add("A");
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync("key-3", Request(("A", 1)), CancellationToken.None));

        inventory.OutOfStock.Clear(); // stock arrived; the client retries with the same key
        var order = await service.CreateAsync("key-3", Request(("A", 1)), CancellationToken.None);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_customer_is_a_validation_error(string? customerId)
    {
        var (service, _, _, _) = Create();
        var request = new CreateOrderRequest(customerId, new List<OrderItem> { new("A", 1) });

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(null, request, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_duplicate_and_out_of_range_items_are_validation_errors()
    {
        var (service, _, _, _) = Create();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(null, new CreateOrderRequest("c", new List<OrderItem>()), CancellationToken.None));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(null, Request(("A", 1), ("a", 2)), CancellationToken.None));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(null, Request(("A", 0)), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_releases_stock_once_and_is_idempotent()
    {
        var (service, inventory, publisher, _) = Create();
        var order = await service.CreateAsync(null, Request(("A", 1)), CancellationToken.None);

        var first = await service.CancelAsync(order.Id, CancellationToken.None);
        var second = await service.CancelAsync(order.Id, CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, first.Status);
        Assert.Equal(OrderStatus.Cancelled, second.Status);
        Assert.Equal(1, inventory.ReleaseCalls);
        Assert.Equal(2, publisher.Events.Count); // Confirmed + Cancelled
    }

    [Fact]
    public async Task Unknown_order_is_not_found()
    {
        var (service, _, _, _) = Create();
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
