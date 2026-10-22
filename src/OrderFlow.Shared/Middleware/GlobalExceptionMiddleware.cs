using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OrderFlow.Shared;

/// <summary>
/// One place that turns exceptions into RFC 7807 ProblemDetails responses.
/// Business code throws domain exceptions; clients get consistent, safe error bodies.
/// </summary>
public sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away. Not an error - don't log it as one and don't try to write a body.
            logger.LogInformation("Request cancelled by the client");
            context.Response.StatusCode = 499;
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                // Too late to change the status code or body; let the server abort the connection.
                logger.LogError(ex, "Exception after the response had started");
                throw;
            }

            var (status, title) = ex switch
            {
                DomainValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
                NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
                ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
                BrokenCircuitException => (StatusCodes.Status503ServiceUnavailable, "Dependency unavailable"),
                TimeoutException or HttpRequestException => (StatusCodes.Status502BadGateway, "Upstream dependency failed"),
                _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
            };

            logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, ex, "Request failed with {Status}", status);

            // Never leak internals (stack traces, upstream URLs) for 5xx.
            var detail = status < 500 ? ex.Message : "The request could not be completed. Quote the correlationId when reporting this.";

            await Results.Problem(
                detail: detail,
                statusCode: status,
                title: title,
                extensions: new Dictionary<string, object?> { ["correlationId"] = context.GetCorrelationId() })
                .ExecuteAsync(context);
        }
    }
}
