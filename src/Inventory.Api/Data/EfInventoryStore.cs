using Microsoft.EntityFrameworkCore;
using OrderFlow.Shared;

namespace Inventory.Api.Data;

public sealed class EfInventoryStore(InventoryDbContext db) : IInventoryStore
{
    public async Task<IReadOnlyList<StockDto>> AllAsync(CancellationToken ct = default) =>
        await db.StockItems.AsNoTracking()
            .OrderBy(s => s.Sku)
            .Select(s => new StockDto(s.Sku, s.Name, s.Available))
            .ToListAsync(ct);

    public async Task<StockDto> GetAsync(string sku, CancellationToken ct = default)
    {
        var key = Normalize(sku);
        var item = await db.StockItems.AsNoTracking().FirstOrDefaultAsync(s => s.Sku == key, ct)
                   ?? throw new NotFoundException($"Unknown SKU '{sku}'.");
        return new StockDto(item.Sku, item.Name, item.Available);
    }

    public async Task<int> ReserveAsync(string sku, Guid orderId, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0)
        {
            throw new DomainValidationException("Quantity must be a positive number.");
        }

        var key = Normalize(sku);

        // 1. A retried request for the same order: succeed without touching stock again.
        if (await db.Reservations.AnyAsync(r => r.OrderId == orderId && r.Sku == key, ct))
        {
            return await AvailableAsync(key, ct) ?? 0;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 2. Atomic, guarded deduction. The WHERE clause is the whole trick:
        //    two requests can never both take the last unit, because the database serialises the UPDATEs
        //    and re-checks "Available >= quantity" each time.
        var updated = await db.StockItems
            .Where(s => s.Sku == key && s.Available >= quantity)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Available, s => s.Available - quantity), ct);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            var current = await AvailableAsync(key, ct);
            if (current is null)
            {
                throw new NotFoundException($"Unknown SKU '{sku}'.");
            }

            throw new ConflictException($"Insufficient stock for {key}: requested {quantity}, available {current}.");
        }

        // 3. Record the reservation. The unique index on (OrderId, Sku) is the final guard against duplicates.
        db.Reservations.Add(new ReservationEntity
        {
            OrderId = orderId,
            Sku = key,
            Quantity = quantity,
            ReservedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Roll back the deduction. If the cause was a concurrent duplicate request, it already won.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();

            if (await db.Reservations.AnyAsync(r => r.OrderId == orderId && r.Sku == key, ct))
            {
                return await AvailableAsync(key, ct) ?? 0;
            }

            throw;
        }

        return await AvailableAsync(key, ct) ?? 0;
    }

    /// <summary>Compensating action. Safe to call repeatedly or concurrently.</summary>
    public async Task<int> ReleaseAsync(Guid orderId, CancellationToken ct = default)
    {
        var reservations = await db.Reservations.AsNoTracking()
            .Where(r => r.OrderId == orderId)
            .ToListAsync(ct);

        if (reservations.Count == 0)
        {
            return 0;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var released = 0;

        foreach (var reservation in reservations)
        {
            var id = reservation.Id;
            var sku = reservation.Sku;
            var quantity = reservation.Quantity;

            // Only the caller whose DELETE actually removes the row returns the stock,
            // so two racing releases can never add it back twice.
            var deleted = await db.Reservations.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
            if (deleted == 1)
            {
                await db.StockItems
                    .Where(s => s.Sku == sku)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.Available, s => s.Available + quantity), ct);
                released++;
            }
        }

        await tx.CommitAsync(ct);
        return released;
    }

    private Task<int?> AvailableAsync(string key, CancellationToken ct) =>
        db.StockItems.AsNoTracking()
            .Where(s => s.Sku == key)
            .Select(s => (int?)s.Available)
            .FirstOrDefaultAsync(ct);

    private static string Normalize(string sku) => sku.Trim().ToUpperInvariant();
}