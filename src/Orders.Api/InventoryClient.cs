using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace Orders.Api;

public sealed record ReserveResult(bool Success, string? Reason);

/// <summary>Abstraction over the Inventory microservice - lets OrdersService be unit-tested with a fake.</summary>
public interface IInventoryClient
{
    Task<ReserveResult> ReserveAsync(string sku, int quantity, Guid orderId, CancellationToken ct);
    Task ReleaseAsync(Guid orderId, CancellationToken ct);
}

/// <summary>
/// Typed HttpClient. Retry, circuit breaker, timeout and correlation-id propagation are
/// applied by the handler pipeline registered in Program.cs - this class only knows the contract.
/// </summary>
public sealed class InventoryClient(HttpClient http) : IInventoryClient
{
    public async Task<ReserveResult> ReserveAsync(string sku, int quantity, Guid orderId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/inventory/{Uri.EscapeDataString(sku)}/reserve",
            new { orderId, quantity },
            ct);

        if (response.IsSuccessStatusCode)
        {
            return new ReserveResult(true, null);
        }

        // Business rejections (out of stock, unknown SKU) are expected outcomes, not exceptions.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken: ct);
            return new ReserveResult(false, problem?.Detail ?? response.ReasonPhrase);
        }

        // Anything else (5xx after retries, 401 from a bad key...) is an infrastructure failure: throw.
        response.EnsureSuccessStatusCode();
        return new ReserveResult(false, "Unexpected response from inventory.");
    }

    public async Task ReleaseAsync(Guid orderId, CancellationToken ct)
    {
        using var response = await http.PostAsync($"api/inventory/reservations/{orderId}/release", content: null, ct);
        response.EnsureSuccessStatusCode();
    }
}
