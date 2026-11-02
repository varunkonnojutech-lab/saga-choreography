namespace SagaChoreography.Core;

/// <summary>
/// Every event in the saga carries the same two routing fields:
/// <see cref="EventId"/> is the deduplication key (consumers must be
/// idempotent, because delivery is at-least-once), and
/// <see cref="OrderId"/> is the correlation id — the saga instance id that
/// lets every service tie an event back to the same order.
/// </summary>
public abstract record SagaEvent(Guid EventId, Guid OrderId, DateTime OccurredAt)
{
    protected SagaEvent(Guid orderId) : this(Guid.NewGuid(), orderId, DateTime.UtcNow)
    {
    }
}

/// <summary>Published by the ordering service when a customer checks out.</summary>
public sealed record OrderPlaced(Guid OrderId, string CustomerEmail, decimal TotalAmount)
    : SagaEvent(OrderId);

/// <summary>Published by the payment service after a successful charge.</summary>
public sealed record PaymentSucceeded(Guid OrderId, string ChargeId) : SagaEvent(OrderId);

/// <summary>Published by the payment service when the charge is declined.</summary>
public sealed record PaymentFailed(Guid OrderId, string Reason) : SagaEvent(OrderId);

/// <summary>Published by the shipping service once a shipment is booked.</summary>
public sealed record ShipmentCreated(Guid OrderId, string TrackingNumber) : SagaEvent(OrderId);

/// <summary>Published by the shipping service when it cannot fulfil the order.</summary>
public sealed record ShipmentFailed(Guid OrderId, string Reason) : SagaEvent(OrderId);

/// <summary>
/// Published by the payment service as compensation for a
/// <see cref="ShipmentFailed"/>: the money goes back, then the order is
/// cancelled. This is the "undo" half of the saga.
/// </summary>
public sealed record PaymentRefunded(Guid OrderId, string ChargeId) : SagaEvent(OrderId);
