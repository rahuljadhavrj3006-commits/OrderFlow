using Inventory.Api;
using OrderFlow.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<InventoryStore>();
builder.Services.AddSingleton<ChaosSettings>();
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

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

inventory.MapGet("/", (InventoryStore store) =>
    Results.Ok(store.All().Select(i => new StockDto(i.Sku, i.Name, i.Available))));

inventory.MapGet("/{sku}", (string sku, InventoryStore store) =>
{
    var item = store.Get(sku);
    return Results.Ok(new StockDto(item.Sku, item.Name, item.Available));
});

inventory.MapPost("/{sku}/reserve", (string sku, ReserveRequest request, InventoryStore store) =>
{
    var remaining = store.Reserve(sku, request.OrderId, request.Quantity);
    return Results.Ok(new ReserveResponse(true, null, remaining));
});

inventory.MapPost("/reservations/{orderId:guid}/release", (Guid orderId, InventoryStore store) =>
    Results.Ok(new { orderId, released = store.Release(orderId) }));

app.MapPost("/admin/chaos", (double? failureRate, int? latencyMs, ChaosSettings chaos) =>
{
    if (failureRate is not null) chaos.FailureRate = failureRate.Value;
    if (latencyMs is not null) chaos.LatencyMs = latencyMs.Value;
    return Results.Ok(new { chaos.FailureRate, chaos.LatencyMs });
}).WithTags("Admin");

app.MapHealthChecks("/health");

app.Run();
