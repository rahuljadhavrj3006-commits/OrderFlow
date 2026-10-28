namespace Inventory.Api.Data;

public class StockItemEntity
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public int Available { get; set; }
}

public class ReservationEntity
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public DateTimeOffset ReservedAt { get; set; }
}