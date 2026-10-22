using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OrderFlow.Shared;

/// <summary>
/// Reads X-Correlation-ID from the request (or generates one), echoes it on the response and
/// puts it in the logging scope so every log line of a request - across services - can be joined.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Guid.NewGuid().ToString("N");
        }
        else if (id.Length > MaxLength)
        {
            // Never trust client input blindly: cap the length so it can't bloat logs.
            id = id[..MaxLength];
        }

        context.Items[HeaderName] = id;
        context.Response.Headers[HeaderName] = id;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }
}

public static class HttpContextExtensions
{
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var value) ? value?.ToString() ?? "" : "";
}
