using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OrderFlow.Shared;

public static class ResilienceRegistration
{
    public static IServiceCollection AddOrderFlowResilience(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResilienceOptions>(configuration.GetSection("Resilience"));
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CircuitBreaker>();

        services.AddTransient<CorrelationIdHandler>();
        services.AddTransient<RetryHandler>();
        services.AddTransient<CircuitBreakerHandler>();
        services.AddTransient<PerAttemptTimeoutHandler>();
        return services;
    }

    /// <summary>
    /// Handler order = outermost first:
    /// correlation -> retry -> circuit breaker -> per-attempt timeout -> network.
    /// Retry sits OUTSIDE the breaker so each retry is also counted by (and can be rejected by) the breaker.
    /// </summary>
    public static IHttpClientBuilder AddOrderFlowResilienceHandlers(this IHttpClientBuilder builder) => builder
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<RetryHandler>()
        .AddHttpMessageHandler<CircuitBreakerHandler>()
        .AddHttpMessageHandler<PerAttemptTimeoutHandler>();
}
