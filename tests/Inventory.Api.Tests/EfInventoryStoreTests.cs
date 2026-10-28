using Inventory.Api.Data;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Shared;

namespace Inventory.Api.Tests;

public sealed class EfInventoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"inventory-test-{Guid.NewGuid():N}.db");

    public EfInventoryStoreTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    private InventoryDbContext NewContext() => new(
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False")
            .Options);

    private async Task<T> WithStore<T>(Func<EfInventoryStore, Task<T>> action)
    {
        await using var db = NewContext();
        return await action(new EfInventoryStore(db));
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public async Task Concurrent_reservations_never_oversell()
    {
        // SKU-1003 is seeded with 10 units; 50 requests race for them.
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            try
            {
                await WithStore(s => s.ReserveAsync("SKU-1003", Guid.NewGuid(), 1));
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        })));

        Assert.Equal(10, results.Count(ok => ok));
        Assert.Equal(0, (await WithStore(s => s.GetAsync("SKU-1003"))).Available);
    }

    [Fact]
    public async Task Reserving_twice_for_the_same_order_deducts_stock_once()
    {
        var orderId = Guid.NewGuid();
        var before = (await WithStore(s => s.GetAsync("SKU-1001"))).Available;

        await WithStore(s => s.ReserveAsync("SKU-1001", orderId, 5));
        await WithStore(s => s.ReserveAsync("SKU-1001", orderId, 5));

        Assert.Equal(before - 5, (await WithStore(s => s.GetAsync("SKU-1001"))).Available);
    }

    [Fact]
    public async Task Concurrent_duplicate_reservations_deduct_stock_once()
    {
        var orderId = Guid.NewGuid();
        var before = (await WithStore(s => s.GetAsync("SKU-1001"))).Available;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => WithStore(s => s.ReserveAsync("SKU-1001", orderId, 2)))));

        Assert.Equal(before - 2, (await WithStore(s => s.GetAsync("SKU-1001"))).Available);
    }

    [Fact]
    public async Task Release_gives_stock_back_and_is_repeatable()
    {
        var orderId = Guid.NewGuid();
        var before = (await WithStore(s => s.GetAsync("SKU-1002"))).Available;
        await WithStore(s => s.ReserveAsync("SKU-1002", orderId, 3));

        Assert.Equal(1, await WithStore(s => s.ReleaseAsync(orderId)));
        Assert.Equal(0, await WithStore(s => s.ReleaseAsync(orderId)));

        Assert.Equal(before, (await WithStore(s => s.GetAsync("SKU-1002"))).Available);
    }

    [Fact]
    public async Task Insufficient_stock_throws_conflict_and_leaves_stock_untouched()
    {
        await Assert.ThrowsAsync<ConflictException>(() =>
            WithStore(s => s.ReserveAsync("SKU-1003", Guid.NewGuid(), 11)));

        Assert.Equal(10, (await WithStore(s => s.GetAsync("SKU-1003"))).Available);
    }

    [Fact]
    public async Task Unknown_sku_throws_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            WithStore(s => s.ReserveAsync("NOPE", Guid.NewGuid(), 1)));
    }
}