using Inventory.Api;
using OrderFlow.Shared;

namespace Inventory.Api.Tests;

public class InventoryTests
{
    [Fact]
    public void Concurrent_reservations_never_oversell()
    {
        var item = new StockItem("SKU", "Widget", 500);
        var successes = 0;

        Parallel.For(0, 2000, _ =>
        {
            if (item.TryReserve(1)) Interlocked.Increment(ref successes);
        });

        Assert.Equal(500, successes);
        Assert.Equal(0, item.Available);
    }

    [Fact]
    public void Reserving_twice_for_the_same_order_deducts_stock_once()
    {
        var store = new InventoryStore();
        var orderId = Guid.NewGuid();
        var before = store.Get("SKU-1001").Available;

        store.Reserve("SKU-1001", orderId, 5);
        store.Reserve("SKU-1001", orderId, 5); // a retried request

        Assert.Equal(before - 5, store.Get("SKU-1001").Available);
    }

    [Fact]
    public void Concurrent_duplicate_reservations_deduct_stock_once()
    {
        var store = new InventoryStore();
        var orderId = Guid.NewGuid();
        var before = store.Get("SKU-1001").Available;

        Parallel.For(0, 50, _ => store.Reserve("SKU-1001", orderId, 2));

        Assert.Equal(before - 2, store.Get("SKU-1001").Available);
    }

    [Fact]
    public void Release_gives_stock_back_and_is_repeatable()
    {
        var store = new InventoryStore();
        var orderId = Guid.NewGuid();
        var before = store.Get("SKU-1002").Available;
        store.Reserve("SKU-1002", orderId, 3);

        Assert.Equal(1, store.Release(orderId));
        Assert.Equal(0, store.Release(orderId)); // second call is a harmless no-op

        Assert.Equal(before, store.Get("SKU-1002").Available);
    }

    [Fact]
    public void Insufficient_stock_throws_conflict_and_leaves_stock_untouched()
    {
        var store = new InventoryStore();
        var before = store.Get("SKU-1003").Available;

        Assert.Throws<ConflictException>(() => store.Reserve("SKU-1003", Guid.NewGuid(), before + 1));

        Assert.Equal(before, store.Get("SKU-1003").Available);
    }

    [Fact]
    public void Unknown_sku_throws_not_found()
    {
        var store = new InventoryStore();
        Assert.Throws<NotFoundException>(() => store.Reserve("NOPE", Guid.NewGuid(), 1));
    }
}
