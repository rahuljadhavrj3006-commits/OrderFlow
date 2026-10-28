using Microsoft.EntityFrameworkCore;

namespace Orders.Api.Data;

public sealed class EfOrderRepository(OrdersDbContext db) : IOrderRepository
{
    public async Task AddAsync(Order order, CancellationToken ct = default)
    {
        db.Orders.Add(ToEntity(order));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Order?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<bool> TryUpdateAsync(Order newValue, Order comparison, CancellationToken ct = default)
    {
        var entity = await db.Orders.FirstOrDefaultAsync(o => o.Id == newValue.Id, ct);
        if (entity is null || entity.Version != comparison.Version)
        {
            return false; // someone else already changed it
        }

        entity.Status = newValue.Status;
        entity.Version = newValue.Version;

        try
        {
            // EF emits: UPDATE ... WHERE Id = @id AND Version = @versionWeRead
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request won the race between our read and our write.
            db.ChangeTracker.Clear(); // drop the stale entity so the caller's retry re-reads fresh data
            return false;
        }
    }

    public async Task<PagedResult<Order>> QueryAsync(OrderStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Orders.AsNoTracking();
        if (status is not null)
        {
            var wanted = status.Value;
            query = query.Where(o => o.Status == wanted);
        }

        var total = await query.CountAsync(ct);
        var entities = await query
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Order>(entities.Select(ToDomain).ToList(), page, pageSize, total);
    }

    private static OrderEntity ToEntity(Order o) => new()
    {
        Id = o.Id,
        CustomerId = o.CustomerId,
        Status = o.Status,
        CreatedAt = o.CreatedAt,
        Version = o.Version,
        Items = o.Items.Select(i => new OrderItemEntity { Sku = i.Sku, Quantity = i.Quantity }).ToList()
    };

    private static Order ToDomain(OrderEntity e) => new(
        e.Id,
        e.CustomerId,
        e.Items.Select(i => new OrderItem(i.Sku, i.Quantity)).ToList(),
        e.Status,
        e.CreatedAt,
        e.Version);
}