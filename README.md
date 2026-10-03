# Order Saga Demo (Choreography Pattern)

An ASP.NET Core (.NET 10) app demonstrating the **choreography-based Saga pattern** for a distributed
order workflow, with **SQL Server** persistence (EF Core) and an event **pub/sub** bus (maps to an
Azure Service Bus **Topic** with one subscription per service).

This is the counterpart to the **orchestration** app in the repository root
([see root README](../README.md)) — same business logic, no central controller.

## How choreography differs

There is **no orchestrator**. Each service subscribes to the events it cares about and publishes its own
outcome events. The end-to-end saga *emerges* from this chain of reactions.

```
OrderCreated ─▶ Inventory ─▶ InventoryReserved ─▶ Payment ─▶ PaymentCharged ─▶ Shipping ─▶ ShipmentCreated
												   │                             │
									   PaymentFailed                    ShippingFailed
												   ▼                             ▼
										InventoryReleased              PaymentRefunded ─▶ InventoryReleased
```

| Service | Subscribes to | Publishes |
|---------|---------------|-----------|
| `InventoryService` | `OrderCreated`, `PaymentFailed`, `PaymentRefunded` | `InventoryReserved` / `InventoryFailed` / `InventoryReleased` |
| `PaymentService` | `InventoryReserved`, `ShippingFailed` | `PaymentCharged` / `PaymentFailed` / `PaymentRefunded` |
| `ShippingService` | `PaymentCharged` | `ShipmentCreated` / `ShippingFailed` |
| `OrderStatusService` | terminal events | updates the order's final `Completed` / `Cancelled` status |

Every reaction is written to the `SagaLog` table, and services are **idempotent** so at-least-once
delivery is safe.

## Code layout

- `Services/Services.cs` — the four autonomous event-driven services (each an `IHostedService`)
- `Messaging/EventBus.cs` — event contracts + `IEventBus` (in-memory pub/sub; maps to a Service Bus Topic)
- `Data/SagaDbContext.cs`, `Domain/Entities.cs` — EF Core context, entities, and seeded products

## Run

1. SQL Server: default is LocalDB (`ConnectionStrings:SagaDb`, database `SagaChoreographyDemo`). The schema
   and seed products are created on startup.
2. Messaging: an in-memory event bus is used out of the box. To use Azure Service Bus, create a topic and a
   subscription per service and point `IEventBus` at it.
3. Run:
   ```
   dotnet run --project SagaChoreography/SagaChoreography.csproj
   ```

## Try the scenarios

Seed stock: LAPTOP=10, PHONE=5, MOUSE=100.

```
# Success
POST /orders {"sku":"MOUSE","quantity":2,"amount":40,"address":"1 Main St"}
# Payment fails (amount > 1000) -> inventory released, order Cancelled
POST /orders {"sku":"LAPTOP","quantity":1,"amount":1500,"address":"1 Main St"}
# Shipping fails (address contains FAIL) -> payment refunded, inventory released
POST /orders {"sku":"PHONE","quantity":1,"amount":500,"address":"FAIL street"}
# Inventory fails -> cancelled immediately
POST /orders {"sku":"PHONE","quantity":99,"amount":10,"address":"1 Main St"}
```

Then `GET /orders/{id}` to see the final status and the full saga log (grouped by service), and
`GET /products` to confirm stock was restored after compensation.

## API

- `POST /orders` — publishes `OrderCreated` and returns; the services drive the rest
- `GET /orders/{id}` — order status + full saga log
- `GET /orders` — recent orders
- `GET /products` — current stock
