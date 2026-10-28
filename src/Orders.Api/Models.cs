namespace Orders.Api;

public enum OrderStatus { Confirmed, Cancelled }

public sealed record OrderItem(string Sku, int Quantity);

public sealed record Order(
    Guid Id,
    string CustomerId,
    IReadOnlyList<OrderItem> Items,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    Guid Version=default);

/// <summary>Nullable members on purpose: we validate ourselves and return a friendly 400 instead of a binder error.</summary>
public sealed record CreateOrderRequest(string? CustomerId, List<OrderItem>? Items);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
