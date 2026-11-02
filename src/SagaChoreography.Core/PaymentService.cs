namespace SagaChoreography.Core;

/// <summary>
/// The payment service. It reacts to <see cref="OrderPlaced"/> by charging
/// the customer, and it owns the compensation step: when shipping fails it
/// refunds the charge it took, so the saga can unwind cleanly.
/// </summary>
public sealed class PaymentService : SagaParticipant
{
    private readonly IEventBus _bus;
    private readonly HashSet<Guid> _failPaymentFor;
    private readonly Dictionary<Guid, string> _charges = new();
    private readonly HashSet<Guid> _refunded = new();

    /// <param name="failPaymentFor">
    /// Order ids whose charge must be declined — the demo and tests use this
    /// to force the payment-failure path without a real payment gateway.
    /// </param>
    public PaymentService(IEventBus bus, IEnumerable<Guid>? failPaymentFor = null)
    {
        _bus = bus;
        _failPaymentFor = failPaymentFor?.ToHashSet() ?? new HashSet<Guid>();
        bus.Subscribe<OrderPlaced>(OnOrderPlaced);
        bus.Subscribe<ShipmentFailed>(OnShipmentFailed);
    }

    public IReadOnlyDictionary<Guid, string> Charges => _charges;
    public IReadOnlyCollection<Guid> RefundedOrders => _refunded;

    /// <summary>Decline the charge for an order that was previously set to succeed.</summary>
    public void FailPaymentFor(Guid orderId) => _failPaymentFor.Add(orderId);

    private async Task OnOrderPlaced(OrderPlaced e)
    {
        if (!MarkSeen(e))
        {
            return; // Redelivery: never charge twice for the same order.
        }

        if (_failPaymentFor.Contains(e.OrderId))
        {
            await _bus.PublishAsync(new PaymentFailed(e.OrderId, "Card declined."));
            return;
        }

        var chargeId = $"ch_{e.OrderId:N}"[..12];
        _charges[e.OrderId] = chargeId;
        await _bus.PublishAsync(new PaymentSucceeded(e.OrderId, chargeId));
    }

    private async Task OnShipmentFailed(ShipmentFailed e)
    {
        if (!MarkSeen(e))
        {
            return;
        }

        // Compensation: refund only if this service actually took money,
        // and only once — a redelivered ShipmentFailed must not refund twice.
        if (_charges.TryGetValue(e.OrderId, out var chargeId) && _refunded.Add(e.OrderId))
        {
            await _bus.PublishAsync(new PaymentRefunded(e.OrderId, chargeId));
        }
    }
}
