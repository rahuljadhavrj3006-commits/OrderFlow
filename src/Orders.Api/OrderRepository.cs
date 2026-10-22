using System.Collections.Concurrent;

namespace Orders.Api;

public interface IOrderRepository
{
    void Add(Order order);
    Order? Get(Guid id);

    /// <summary>Optimistic update: succeeds only if the stored order is still <paramref name="comparison"/>.</summary>
    bool TryUpdate(Order newValue, Order comparison);

    PagedResult<Order> Query(OrderStatus? status, int page, int pageSize);
}

/// <summary>In-memory stand-in for a database (swap for EF Core + SQL later without touching the service).</summary>
public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();

    public void Add(Order order) => _orders[order.Id] = order;

    public Order? Get(Guid id) => _orders.TryGetValue(id, out var order) ? order : null;

    public bool TryUpdate(Order newValue, Order comparison) => _orders.TryUpdate(newValue.Id, newValue, comparison);

    public PagedResult<Order> Query(OrderStatus? status, int page, int pageSize)
    {
        var query = _orders.Values.AsEnumerable();
        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        var all = query.OrderByDescending(o => o.CreatedAt).ToList();
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PagedResult<Order>(items, page, pageSize, all.Count);
    }
}
