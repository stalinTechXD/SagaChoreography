using Microsoft.EntityFrameworkCore;
using SagaChoreography.Data;
using SagaChoreography.Domain;
using SagaChoreography.Messaging;
using SagaChoreography.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<SagaDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("SagaDb")));

builder.Services.AddSingleton<IEventBus>(sp =>
{
    var cs = builder.Configuration["ServiceBus:ConnectionString"];
    return string.IsNullOrWhiteSpace(cs)
        ? new InMemoryEventBus(sp.GetRequiredService<ILogger<InMemoryEventBus>>())
        : new ServiceBusTopicEventBus(cs, sp.GetRequiredService<ILogger<ServiceBusTopicEventBus>>());
});

// Each participant is an independent subscriber. No orchestrator is registered.
builder.Services.AddHostedService<InventoryService>();
builder.Services.AddHostedService<PaymentService>();
builder.Services.AddHostedService<ShippingService>();
builder.Services.AddHostedService<OrderStatusService>();
// Started last so the bus begins processing only after all subscriptions are registered.
builder.Services.AddHostedService<EventBusHost>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<SagaDbContext>().Database.EnsureCreatedAsync();

app.MapGet("/", () => "Choreography saga demo. POST /orders, GET /orders/{id}, GET /products");

app.MapPost("/orders", async (CreateOrderRequest req, SagaDbContext db, IEventBus bus) =>
{
    var order = new Order { Id = Guid.NewGuid(), Sku = req.Sku, Quantity = req.Quantity, Amount = req.Amount, Address = req.Address };
    db.Orders.Add(order);
    db.SagaLog.Add(new SagaLogEntry { OrderId = order.Id, Service = "Order", Message = "OrderCreated event published" });
    await db.SaveChangesAsync();
    // Just raise the first event and step back — the services take it from here.
    await bus.PublishAsync(new OrderCreated(order.Id, order.Sku, order.Quantity, order.Amount, order.Address));
    return Results.Accepted($"/orders/{order.Id}", new { order.Id, order.Status });
});

app.MapGet("/orders/{id:guid}", async (Guid id, SagaDbContext db) =>
{
    var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
    if (order is null) return Results.NotFound();
    var log = await db.SagaLog.AsNoTracking().Where(l => l.OrderId == id)
        .OrderBy(l => l.Id).Select(l => new { l.At, l.Service, l.Message }).ToListAsync();
    return Results.Ok(new { order, log });
});

app.MapGet("/orders", async (SagaDbContext db) =>
    await db.Orders.AsNoTracking().OrderByDescending(o => o.CreatedAt).Take(50).ToListAsync());

app.MapGet("/products", async (SagaDbContext db) => await db.Products.AsNoTracking().ToListAsync());

app.Run();

record CreateOrderRequest(string Sku, int Quantity, decimal Amount, string Address);
