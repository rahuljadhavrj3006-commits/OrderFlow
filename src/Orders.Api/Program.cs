using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OrderFlow.Shared;
using Orders.Api;

var builder = WebApplication.CreateBuilder(args);

// ---- Resilient typed HttpClient to the Inventory microservice ----
builder.Services.AddOrderFlowResilience(builder.Configuration);
builder.Services.AddHttpClient<IInventoryClient, InventoryClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Services:Inventory:BaseUrl"]
        ?? throw new InvalidOperationException("Services:Inventory:BaseUrl is not configured."));
    client.DefaultRequestHeaders.Add("X-Api-Key", config["ServiceAuth:ApiKey"]
        ?? throw new InvalidOperationException("ServiceAuth:ApiKey is not configured."));
    client.Timeout = TimeSpan.FromSeconds(30); // overall budget across all retries
}).AddOrderFlowResilienceHandlers();

// ---- In-process event bus: bounded channel + background worker ----
builder.Services.AddSingleton(Channel.CreateBounded<OrderEvent>(
    new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait }));
builder.Services.AddSingleton<IEventPublisher, ChannelEventPublisher>();
builder.Services.AddSingleton<OrderStats>();
builder.Services.AddHostedService<OrderEventProcessor>();

// ---- Application services. Lifetimes are deliberate: state lives in singletons; OrdersService is scoped
// because it depends on a typed HttpClient (a singleton must not capture one: it would never see DNS changes).
builder.Services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddScoped<OrdersService>();

// ---- Rate limiting (built into ASP.NET Core 7+) ----
static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global safety net: 100 requests / minute / client.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Stricter policy for the expensive write endpoint: token bucket allows short bursts, then ~0.5 req/s sustained.
    options.AddPolicy("create-order", ctx =>
        RateLimitPartition.GetTokenBucketLimiter(ClientKey(ctx), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 10,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ---- Middleware pipeline (order matters) ----
app.UseOrderFlowCore();   // correlation id -> timing -> exception handling
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseRateLimiter();     // reject abusive clients before doing any real work

// ---- Endpoints ----
var orders = app.MapGroup("/api/v1/orders").WithTags("Orders");

orders.MapPost("/", async (
    CreateOrderRequest request,
    OrdersService service,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    CancellationToken ct) =>
{
    var order = await service.CreateAsync(idempotencyKey, request, ct);
    return Results.Created($"/api/v1/orders/{order.Id}", order);
}).RequireRateLimiting("create-order");

orders.MapGet("/{id:guid}", (Guid id, OrdersService service) => Results.Ok(service.Get(id)));

orders.MapGet("/", (OrdersService service, OrderStatus? status, int page = 1, int pageSize = 20) =>
    Results.Ok(service.Query(status, page, pageSize)));

orders.MapDelete("/{id:guid}", async (Guid id, OrdersService service, CancellationToken ct) =>
    Results.Ok(await service.CancelAsync(id, ct)));

app.MapGet("/api/v1/diagnostics", (CircuitBreaker breaker, OrderStats stats) =>
    Results.Ok(new { circuit = breaker.State.ToString(), eventsProcessed = stats.Processed })).WithTags("Diagnostics");

app.MapHealthChecks("/health");

app.Run();
