namespace Inventory.Api.Data;

public interface IInventoryStore
{
    Task<IReadOnlyList<StockDto>> AllAsync(CancellationToken ct = default);
    Task<StockDto> GetAsync(string sku, CancellationToken ct = default);
    Task<int> ReserveAsync(string sku, Guid orderId, int quantity, CancellationToken ct = default);
    Task<int> ReleaseAsync(Guid orderId, CancellationToken ct = default);
}