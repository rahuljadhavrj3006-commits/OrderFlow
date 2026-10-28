using Inventory.Api;
using Inventory.Api.Data;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Shared;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "inventory.db");
builder.Services.AddDbContext<InventoryDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<IInventoryStore, EfInventoryStore>();
builder.Services.AddSingleton<ChaosSettings>();
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.Migrate();
}
// ---- Middleware pipeline: ORDER MATTERS. Requests flow top -> bottom, responses bottom -> top. ----
app.UseOrderFlowCore();                       // correlation id -> timing -> exception handling
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();                         // before auth so the docs are reachable
    app.UseSwaggerUI();
}
app.UseApiKey();                              // internal service: reject callers without the key
app.UseMiddleware<ChaosMiddleware>();         // fault injection (after auth so strangers can't trigger it)

// ---- Endpoints ----
var inventory = app.MapGroup("/api/inventory").WithTags("Inventory");

inventory.MapGet("/", async (IInventoryStore store, CancellationToken ct) =>
    Results.Ok(await store.AllAsync(ct)));

inventory.MapGet("/{sku}", async (string sku, IInventoryStore store, CancellationToken ct) =>
    Results.Ok(await store.GetAsync(sku, ct)));

inventory.MapPost("/{sku}/reserve", async (string sku, ReserveRequest request, IInventoryStore store, CancellationToken ct) =>
{
    var remaining = await store.ReserveAsync(sku, request.OrderId, request.Quantity, ct);
    return Results.Ok(new ReserveResponse(true, null, remaining));
});

inventory.MapPost("/reservations/{orderId:guid}/release", async (Guid orderId, IInventoryStore store, CancellationToken ct) =>
    Results.Ok(new { orderId, released = await store.ReleaseAsync(orderId, ct) }));

app.MapPost("/admin/chaos", (double? failureRate, int? latencyMs, ChaosSettings chaos) =>
{
    if (failureRate is not null) chaos.FailureRate = failureRate.Value;
    if (latencyMs is not null) chaos.LatencyMs = latencyMs.Value;
    return Results.Ok(new { chaos.FailureRate, chaos.LatencyMs });
}).WithTags("Admin");

app.MapHealthChecks("/health");

app.Run();
