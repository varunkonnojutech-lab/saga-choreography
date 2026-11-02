# Saga Choreography in .NET — ordering → payment → shipping, with no boss

[![Build](https://github.com/varunkonnojutech-lab/saga-choreography/actions/workflows/ci.yml/badge.svg)](https://github.com/varunkonnojutech-lab/saga-choreography/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/varunkonnojutech-lab/saga-choreography)

One customer checkout touches three services: ordering takes the order, payment charges the card, shipping books the delivery. A single database transaction cannot span them — they are separate services with separate databases. So how does the checkout either complete everywhere or unwind everywhere, with no step left half-done?

The saga pattern answers that, and **choreography** is its decentralized form: there is no orchestrator service telling everyone what to do. Each service watches for events, does its step, and publishes the next event. The workflow emerges from the event flow — ordering publishes `OrderPlaced`, payment reacts by charging and publishing `PaymentSucceeded`, shipping reacts by booking and publishing `ShipmentCreated`. And when a step fails, the services run **compensating actions** that undo the steps already taken: shipping fails → payment refunds → order cancels.

## Who is this for

.NET developers building microservices where one business operation spans several services — checkout, booking, onboarding — and who have felt the pain of "the payment went through but the order never shipped" (or the reverse). If you read the [outbox-pattern](https://github.com/varunkonnojutech-lab/outbox-pattern) repo, this is the natural next step: the outbox guarantees an event *leaves* a service reliably; the saga guarantees the multi-service workflow *completes*.

## What you'll learn

- **Why distributed transactions don't work here** — two-phase commit across services couples availability and latency, and is effectively unavailable in most microservice stacks. Sagas trade atomicity for eventual consistency plus explicit undo steps.
- **Choreography** — no central controller: each service subscribes to the event that triggers its step and publishes the event that triggers the next one. The "workflow" is the event chain, visible in the event log.
- **Compensating transactions** — the saga's undo button. A refund undoes a charge; a cancellation undoes an order. Every forward step that has a side effect needs a compensating step, designed *before* the failure happens.
- **Why idempotency is non-negotiable** — brokers deliver at-least-once, so every handler must ignore an event it already processed. A redelivered `OrderPlaced` must never charge twice; a redelivered `ShipmentFailed` must never refund twice. Every service here dedupes on the event id.
- **The correlation id** — every event carries the `OrderId`, so each service can tie events from different services back to the same saga instance.
- **Choreography vs. orchestration** — choreography is simpler to start (no new service, just events) but harder to see: the workflow lives nowhere in one place, so debugging means reading the event log. Orchestration centralizes the workflow in one service at the cost of that service becoming a coupling point. The comparison table below helps you choose.

## Quick start

You need .NET 8. No Docker, no broker, no external services — the bus is in-memory:

```bash
dotnet test
dotnet run --project src/SagaChoreography.Demo
```

## What the demo shows

1. **Happy path** — `OrderPlaced` → `PaymentSucceeded` → `ShipmentCreated`, order ends `Shipped`.
2. **Payment declined** — `OrderPlaced` → `PaymentFailed`, order ends `Cancelled`. Nothing ships, nothing is charged.
3. **Shipping failure, compensated** — charged, then out of stock: `ShipmentFailed` → `PaymentRefunded`, order ends `Cancelled`. The money goes back; the saga unwinds instead of stranding a paid-but-unshipped order.
4. **Duplicate delivery** — the happy path's events are re-delivered. Idempotent handlers ignore them: still one charge, one shipment, order still `Shipped`.

## The saga, in one picture

```
Happy path:
  OrderService ──OrderPlaced──────► PaymentService ──PaymentSucceeded──► ShippingService
       ▲                                                                      │
       └────────────────────────── ShipmentCreated ───────────────────────────┘
       order: Placed → Paid → Shipped

Payment failure (nothing to undo):
  OrderService ──OrderPlaced──────► PaymentService ──PaymentFailed──► OrderService
       order: Placed → Cancelled

Shipping failure (compensation):
  ... PaymentSucceeded → ShippingService ──ShipmentFailed──► PaymentService
                                                              │ PaymentRefunded
                                                              ▼
                                                        OrderService
       order: Placed → Paid → Cancelled, charge refunded
```

And the failure cases, each handled:

| What goes wrong | What happens |
|---|---|
| Card declined | `PaymentFailed` cancels the order; no shipment is ever booked |
| Item out of stock after payment | `ShipmentFailed` triggers a refund, then the order cancels — no paid-but-unshipped order |
| Event delivered twice | Each handler dedupes on the event id — no double charge, shipment, or refund |
| Event for an unknown order | The ordering service ignores it; no phantom state is created |

## Choreography vs. orchestration

| | Choreography (this repo) | Orchestration |
|---|---|---|
| Workflow lives in | The event chain, spread across services | One orchestrator service |
| Adding a step | Subscribe to an event, publish the next | Change the orchestrator |
| Seeing the whole flow | Read the event log | Read the orchestrator's code/state |
| Failure handling | Each service publishes its failure; compensations chain back | Orchestrator runs compensations in reverse order |
| Best when | Few steps, stable flow, teams own their services | Many steps, complex branching, flow changes often |

## The pieces

| File | Role |
|---|---|
| `src/SagaChoreography.Core/Events.cs` | The six saga events; every one carries `EventId` (dedup key) and `OrderId` (correlation id) |
| `src/SagaChoreography.Core/InMemoryEventBus.cs` | `IEventBus`, the in-memory bus with an audit trail, plus the `SagaParticipant` dedup base class |
| `src/SagaChoreography.Core/OrderService.cs` | Starts the saga, tracks order status, cancels on failure or refund |
| `src/SagaChoreography.Core/PaymentService.cs` | Charges on `OrderPlaced`, refunds on `ShipmentFailed` (compensation) |
| `src/SagaChoreography.Core/ShippingService.cs` | Ships on `PaymentSucceeded`, reports `ShipmentFailed` when it cannot fulfil |

## Production notes

- **Real broker, same services.** Swap `InMemoryEventBus` for RabbitMQ, Kafka, or Azure Service Bus behind `IEventBus` — the services and tests do not change. (Pair it with the transactional outbox so publishing an event is atomic with the local database write.)
- **Compensations are business decisions, not just code.** A refund policy, a restocking rule, a "contact the customer" step — agree on each undo with the business before the failure happens, not during it.
- **Make compensations idempotent too.** A refund that runs twice is a new bug, not a recovery. Dedup on the triggering event id, as `PaymentService` does here.
- **Observability is the choreography tax.** With no orchestrator, the event log *is* the workflow trace: log every event with its correlation id, and alert on sagas stuck in an intermediate state past a timeout (e.g. `Paid` for over an hour with no shipment event).
- **When to switch to orchestration.** Once the flow grows branching (partial shipments, split payments, retries with approval), a central orchestrator with explicit saga state becomes easier to reason about than a web of events. Choreography is the right start, not necessarily the final shape.
- **Timeouts and stuck sagas.** This demo runs synchronously; a real saga needs a timeout per step (a payment that never answers is a failure too) and a reaper that compensates sagas idle past their deadline.

## Next steps

- Put a real broker behind `IEventBus` and run each service as its own process.
- Add a saga timeout: an order stuck in `Paid` with no shipment event after N minutes triggers the refund path automatically.
- Build the orchestration variant of the same checkout and compare the two event logs side by side.
- Combine with the outbox pattern: each service writes its outgoing event to its own outbox table, so a crash between the local write and the publish loses nothing.
