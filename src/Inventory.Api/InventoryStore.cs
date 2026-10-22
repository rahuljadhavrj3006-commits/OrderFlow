using System.Collections.Concurrent;
using OrderFlow.Shared;

namespace Inventory.Api;

public sealed class InventoryStore
{
    private readonly ConcurrentDictionary<string, StockItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(Guid OrderId, string Sku), Reservation> _reservations = new();

    public InventoryStore()
    {
        Seed("SKU-1001", "Mechanical Keyboard", 100);
        Seed("SKU-1002", "27\" Monitor", 50);
        Seed("SKU-1003", "USB-C Dock", 10);
        Seed("SKU-1004", "Limited Edition Mug", 0);
    }

    private void Seed(string sku, string name, int quantity) => _items[sku] = new StockItem(sku, name, quantity);

    public IReadOnlyList<StockItem> All() => _items.Values.OrderBy(i => i.Sku).ToList();

    public StockItem Get(string sku) =>
        _items.TryGetValue(sku, out var item) ? item : throw new NotFoundException($"Unknown SKU '{sku}'.");

    /// <summary>
    /// Idempotent per (orderId, sku): calling it twice for the same order reserves stock once.
    /// That is what makes it safe for the client to retry after a timeout.
    /// </summary>
    public int Reserve(string sku, Guid orderId, int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainValidationException("Quantity must be a positive number.");
        }

        var item = Get(sku);
        var key = (orderId, item.Sku);

        if (_reservations.ContainsKey(key))
        {
            return item.Available; // already reserved by an earlier (retried) request
        }

        if (!item.TryReserve(quantity))
        {
            throw new ConflictException($"Insufficient stock for {item.Sku}: requested {quantity}, available {item.Available}.");
        }

        if (!_reservations.TryAdd(key, new Reservation(orderId, item.Sku, quantity, DateTimeOffset.UtcNow)))
        {
            // A duplicate request won the race to record the reservation: undo our extra deduction.
            item.Release(quantity);
        }

        return item.Available;
    }

    /// <summary>Compensating action: gives back everything reserved for an order. Safe to call repeatedly.</summary>
    public int Release(Guid orderId)
    {
        var released = 0;
        foreach (var key in _reservations.Keys.Where(k => k.OrderId == orderId).ToList())
        {
            if (_reservations.TryRemove(key, out var reservation))
            {
                _items[reservation.Sku].Release(reservation.Quantity);
                released++;
            }
        }
        return released;
    }
}
