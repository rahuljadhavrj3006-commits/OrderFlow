using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderFlow.Shared;

internal static class Transient
{
    public static bool IsTransient(Exception ex) => ex is HttpRequestException or TimeoutException;

    public static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

/// <summary>Forwards the incoming X-Correlation-ID to outgoing calls so traces span services.</summary>
public sealed class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = accessor.HttpContext?.GetCorrelationId();
        if (!string.IsNullOrEmpty(id) && !request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
        {
            request.Headers.Add(CorrelationIdMiddleware.HeaderName, id);
        }
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Retries transient failures with exponential backoff + jitter.
/// Jitter matters: without it, many clients retry in lock-step and hammer the recovering service together.
/// Only safe for idempotent operations - which is why Inventory reservations are keyed by (orderId, sku).
/// </summary>
public sealed class RetryHandler(IOptions<ResilienceOptions> options, ILogger<RetryHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var o = options.Value;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (!Transient.IsTransient(response.StatusCode) || attempt >= o.MaxRetries)
                {
                    return response;
                }

                logger.LogWarning("Attempt {Attempt} returned {Status}; will retry", attempt + 1, (int)response.StatusCode);
                response.Dispose();
            }
            catch (Exception ex) when (Transient.IsTransient(ex) && attempt < o.MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Attempt {Attempt} failed ({Type}); will retry", attempt + 1, ex.GetType().Name);
            }

            var delayMs = o.BaseDelayMs * Math.Pow(2, attempt) * (0.5 + Random.Shared.NextDouble());
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
        }
    }
}

/// <summary>Fails fast while the circuit is open; records success/failure of every call that is let through.</summary>
public sealed class CircuitBreakerHandler(CircuitBreaker breaker) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!breaker.AllowRequest())
        {
            throw new BrokenCircuitException("The circuit is open: the downstream service is considered unhealthy.");
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                breaker.OnFailure();
            }
            else
            {
                breaker.OnSuccess();
            }
            return response;
        }
        catch (Exception ex) when (Transient.IsTransient(ex))
        {
            breaker.OnFailure();
            throw;
        }
    }
}

/// <summary>
/// Per-attempt timeout. HttpClient.Timeout covers the whole pipeline including retries,
/// so a separate per-attempt limit is needed to stop one slow call from eating the whole budget.
/// </summary>
public sealed class PerAttemptTimeoutHandler(IOptions<ResilienceOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));

        try
        {
            return await base.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // It was our timeout, not the caller's cancellation: surface it as a transient failure.
            throw new TimeoutException($"The call exceeded the {options.Value.TimeoutSeconds}s per-attempt timeout.");
        }
    }
}
