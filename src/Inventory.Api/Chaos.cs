namespace Inventory.Api;

/// <summary>
/// Runtime-adjustable fault injection, used to demonstrate retries, timeouts and the circuit breaker:
///   POST /admin/chaos?failureRate=0.6&amp;latencyMs=300
/// </summary>
public sealed class ChaosSettings
{
    private double _failureRate;
    private int _latencyMs;

    public double FailureRate
    {
        get => Volatile.Read(ref _failureRate);
        set => Volatile.Write(ref _failureRate, Math.Clamp(value, 0, 1));
    }

    public int LatencyMs
    {
        get => Volatile.Read(ref _latencyMs);
        set => Volatile.Write(ref _latencyMs, Math.Max(0, value));
    }
}

public sealed class ChaosMiddleware(RequestDelegate next, ChaosSettings chaos)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health") || path.StartsWithSegments("/admin"))
        {
            await next(context);
            return;
        }

        if (chaos.LatencyMs > 0)
        {
            await Task.Delay(chaos.LatencyMs, context.RequestAborted);
        }

        if (chaos.FailureRate > 0 && Random.Shared.NextDouble() < chaos.FailureRate)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Injected failure (chaos).");
            return;
        }

        await next(context);
    }
}
