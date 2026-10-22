using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace OrderFlow.Shared;

/// <summary>
/// Simple service-to-service authentication using a shared secret in X-Api-Key.
/// Good enough to demonstrate middleware short-circuiting; in production prefer mTLS or OAuth2 client credentials.
/// </summary>
public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        var key = configuration["ServiceAuth:ApiKey"]
                  ?? throw new InvalidOperationException("ServiceAuth:ApiKey is not configured.");
        _expectedKey = Encoding.UTF8.GetBytes(key);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context); // health probes must work without credentials
            return;
        }

        var provided = Encoding.UTF8.GetBytes(context.Request.Headers[HeaderName].ToString());

        // Constant-time comparison so response timing doesn't leak how many characters matched.
        if (!CryptographicOperations.FixedTimeEquals(provided, _expectedKey))
        {
            await Results.Problem(
                    detail: "Missing or invalid API key.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Unauthorized",
                    extensions: new Dictionary<string, object?> { ["correlationId"] = context.GetCorrelationId() })
                .ExecuteAsync(context);
            return; // short-circuit: the rest of the pipeline never runs
        }

        await _next(context);
    }
}
