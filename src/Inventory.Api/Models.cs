namespace Inventory.Api;

/// <summary>
/// Stock for one SKU. Reservation is a lock-free compare-and-swap loop, so
/// thousands of concurrent requests can never drive the count below zero (no oversell).
/// </summary>
public sealed class StockItem(string sku, string name, int initial)
{
    private int _available = initial;

    public string Sku { get; } = sku;
    public string Name { get; } = name;
    public int Available => Volatile.Read(ref _available);

    public bool TryReserve(int quantity)
    {
        while (true)
        {
            var current = Volatile.Read(ref _available);
            if (current < quantity)
            {
                return false;
            }

            // Succeeds only if nobody changed the value since we read it; otherwise loop and re-check.
            if (Interlocked.CompareExchange(ref _available, current - quantity, current) == current)
            {
                return true;
            }
        }
    }

    public void Release(int quantity) => Interlocked.Add(ref _available, quantity);
}

public sealed record Reservation(Guid OrderId, string Sku, int Quantity, DateTimeOffset ReservedAt);

public sealed record ReserveRequest(Guid OrderId, int Quantity);

public sealed record ReserveResponse(bool Success, string? Reason, int Remaining);

public sealed record StockDto(string Sku, string Name, int Available);
