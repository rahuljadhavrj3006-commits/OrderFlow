using System.Collections.Concurrent;

namespace Orders.Api;

public interface IOrderRepository
{
    Task AddAsync(Order order, CancellationToken ct = default);
    Task<Order?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Replaces the order only if the stored version still equals comparison.Version.</summary>
    Task<bool> TryUpdateAsync(Order newValue, Order comparison, CancellationToken ct = default);

    Task<PagedResult<Order>> QueryAsync(OrderStatus? status, int page, int pageSize, CancellationToken ct = default);
}

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();

    public Task AddAsync(Order order, CancellationToken ct = default)
    {
        _orders[order.Id] = order;
        return Task.CompletedTask;
    }

    public Task<Order?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<Order?>(_orders.TryGetValue(id, out var order) ? order : null);

    public Task<bool> TryUpdateAsync(Order newValue, Order comparison, CancellationToken ct = default) =>
        Task.FromResult(_orders.TryUpdate(newValue.Id, newValue, comparison));

    public Task<PagedResult<Order>> QueryAsync(OrderStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _orders.Values.AsEnumerable();
        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        var all = query.OrderByDescending(o => o.CreatedAt).ToList();
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<Order>(items, page, pageSize, all.Count));
    }
}