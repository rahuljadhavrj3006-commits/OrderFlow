namespace Orders.Api.Data;

public class OrderEntity
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";
    public OrderStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Concurrency token: EF adds "WHERE Version = @old" to every UPDATE,
    // so two simultaneous edits can't silently overwrite each other.
    public Guid Version { get; set; } = Guid.NewGuid();

    public List<OrderItemEntity> Items { get; set; } = new();
}

public class OrderItemEntity
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}