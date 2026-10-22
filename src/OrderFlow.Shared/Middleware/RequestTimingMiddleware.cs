using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OrderFlow.Shared;

/// <summary>
/// Measures request duration, adds an X-Response-Time-ms header and warns about slow requests.
/// Note the header is written inside OnStarting: once the response has started, headers are read-only.
/// </summary>
public sealed class RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger)
{
    private const long SlowThresholdMs = 500;

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Response-Time-ms"] = stopwatch.ElapsedMilliseconds.ToString();
            return Task.CompletedTask;
        });

        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var level = stopwatch.ElapsedMilliseconds > SlowThresholdMs ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "{Method} {Path} -> {Status} in {Elapsed}ms",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, stopwatch.ElapsedMilliseconds);
        }
    }
}
