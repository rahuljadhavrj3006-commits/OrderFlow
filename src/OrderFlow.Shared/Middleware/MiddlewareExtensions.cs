using Microsoft.AspNetCore.Builder;

namespace OrderFlow.Shared;

public static class MiddlewareExtensions
{
    /// <summary>
    /// Registers the cross-cutting middleware in a deliberate order:
    /// correlation id first (so everything after can log it), then timing (so it measures everything below),
    /// then exception handling (so errors raised by anything below become ProblemDetails).
    /// </summary>
    public static IApplicationBuilder UseOrderFlowCore(this IApplicationBuilder app) => app
        .UseMiddleware<CorrelationIdMiddleware>()
        .UseMiddleware<RequestTimingMiddleware>()
        .UseMiddleware<GlobalExceptionMiddleware>();

    public static IApplicationBuilder UseApiKey(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiKeyMiddleware>();
}
