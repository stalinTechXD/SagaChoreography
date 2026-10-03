using Microsoft.EntityFrameworkCore;
using SagaChoreography.Data;
using SagaChoreography.Domain;
using SagaChoreography.Messaging;

namespace SagaChoreography.Services;

// Each "service" subscribes to the events it reacts to and publishes its own outcome events.
// There is no orchestrator: the saga emerges from this chain of reactions.
//
// Happy path:  OrderCreated -> InventoryReserved -> PaymentCharged -> ShipmentCreated
// Compensation: PaymentFailed  -> InventoryReleased
//               ShippingFailed -> PaymentRefunded -> InventoryReleased

/// <summary>Base helper that opens a DbContext scope per message (services are independent).</summary>
public abstract class ServiceBase(IServiceScopeFactory scopes)
{
    protected async Task WithDb(Func<SagaDbContext, Task> action)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
        await action(db);
    }

    protected static SagaLogEntry Log(Guid id, string service, string msg) =>
        new() { OrderId = id, Service = service, Message = msg };
}

public sealed class InventoryService(IServiceScopeFactory scopes, IEventBus bus) : ServiceBase(scopes), IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        bus.Subscribe<OrderCreated>(Reserve);
        bus.Subscribe<PaymentFailed>(e => Release(e.OrderId));
        bus.Subscribe<ShippingFailed>(_ => Task.CompletedTask); // handled after PaymentRefunded
        bus.Subscribe<PaymentRefunded>(e => Release(e.OrderId));
        return Task.CompletedTask;
    }

    private Task Reserve(OrderCreated e) => WithDb(async db =>
    {
        var product = await db.Products.FindAsync(e.Sku);
        if (product is null || product.Stock < e.Quantity)
        {
            db.SagaLog.Add(Log(e.OrderId, "Inventory", "Reservation failed: insufficient stock"));
            await db.SaveChangesAsync();
            await bus.PublishAsync(new InventoryFailed(e.OrderId, "Insufficient stock"));
            return;
        }
        product.Stock -= e.Quantity;
        db.Reservations.Add(new InventoryReservation { OrderId = e.OrderId, Sku = e.Sku, Quantity = e.Quantity });
        db.SagaLog.Add(Log(e.OrderId, "Inventory", $"Reserved {e.Quantity} x {e.Sku}"));
        await db.SaveChangesAsync();
        await bus.PublishAsync(new InventoryReserved(e.OrderId));
    });

    private Task Release(Guid orderId) => WithDb(async db =>
    {
        var r = await db.Reservations.FindAsync(orderId);
        if (r is { Released: false })
        {
            (await db.Products.FindAsync(r.Sku))!.Stock += r.Quantity;
            r.Released = true;
            db.SagaLog.Add(Log(orderId, "Inventory", "Compensated: stock released"));
            await db.SaveChangesAsync();
            await bus.PublishAsync(new InventoryReleased(orderId));
        }
    });

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class PaymentService(IServiceScopeFactory scopes, IEventBus bus) : ServiceBase(scopes), IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        bus.Subscribe<InventoryReserved>(Charge);
        bus.Subscribe<ShippingFailed>(e => Refund(e.OrderId));
        return Task.CompletedTask;
    }

    private Task Charge(InventoryReserved e) => WithDb(async db =>
    {
        var order = await db.Orders.FindAsync(e.OrderId);
        if (order!.Amount > 1000)
        {
            db.SagaLog.Add(Log(e.OrderId, "Payment", "Charge declined: amount over 1000"));
            await db.SaveChangesAsync();
            await bus.PublishAsync(new PaymentFailed(e.OrderId, "Card declined (amount over 1000)"));
            return;
        }
        db.Payments.Add(new Payment { OrderId = e.OrderId, Amount = order.Amount });
        db.SagaLog.Add(Log(e.OrderId, "Payment", $"Charged {order.Amount:C}"));
        await db.SaveChangesAsync();
        await bus.PublishAsync(new PaymentCharged(e.OrderId));
    });

    private Task Refund(Guid orderId) => WithDb(async db =>
    {
        var p = await db.Payments.FindAsync(orderId);
        if (p is { Refunded: false })
        {
            p.Refunded = true;
            db.SagaLog.Add(Log(orderId, "Payment", "Compensated: payment refunded"));
            await db.SaveChangesAsync();
            await bus.PublishAsync(new PaymentRefunded(orderId));
        }
    });

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class ShippingService(IServiceScopeFactory scopes, IEventBus bus) : ServiceBase(scopes), IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        bus.Subscribe<PaymentCharged>(Ship);
        return Task.CompletedTask;
    }

    private Task Ship(PaymentCharged e) => WithDb(async db =>
    {
        var order = await db.Orders.FindAsync(e.OrderId);
        if (order!.Address.Contains("FAIL", StringComparison.OrdinalIgnoreCase))
        {
            db.SagaLog.Add(Log(e.OrderId, "Shipping", "Shipment failed: address not serviceable"));
            await db.SaveChangesAsync();
            await bus.PublishAsync(new ShippingFailed(e.OrderId, "Address not serviceable"));
            return;
        }
        var tracking = $"TRK-{Guid.NewGuid():N}"[..16];
        db.Shipments.Add(new Shipment { OrderId = e.OrderId, Address = order.Address, TrackingNumber = tracking });
        db.SagaLog.Add(Log(e.OrderId, "Shipping", $"Shipment created {tracking}"));
        await db.SaveChangesAsync();
        await bus.PublishAsync(new ShipmentCreated(e.OrderId, tracking));
    });

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Updates the order's final business status by observing terminal events.</summary>
public sealed class OrderStatusService(IServiceScopeFactory scopes, IEventBus bus) : ServiceBase(scopes), IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        bus.Subscribe<ShipmentCreated>(e => SetStatus(e.OrderId, OrderStatus.Completed, null));
        bus.Subscribe<InventoryFailed>(e => SetStatus(e.OrderId, OrderStatus.Cancelled, e.Reason));
        bus.Subscribe<InventoryReleased>(e => SetStatus(e.OrderId, OrderStatus.Cancelled, null));
        return Task.CompletedTask;
    }

    private Task SetStatus(Guid orderId, string status, string? reason) => WithDb(async db =>
    {
        var order = await db.Orders.FindAsync(orderId);
        if (order is null) return;
        order.Status = status;
        order.FailureReason ??= reason;
        db.SagaLog.Add(Log(orderId, "Order", $"Final status: {status}"));
        await db.SaveChangesAsync();
    });

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
