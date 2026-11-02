namespace SagaChoreography.Core;

/// <summary>
/// The shipping service. It reacts to <see cref="PaymentSucceeded"/> by
/// booking a shipment — never before payment, which is what makes the order
/// of the choreography load-bearing. When it cannot fulfil an order it
/// publishes <see cref="ShipmentFailed"/>, triggering the payment refund.
/// </summary>
public sealed class ShippingService : SagaParticipant
{
    private readonly IEventBus _bus;
    private readonly HashSet<Guid> _failShippingFor;
    private readonly Dictionary<Guid, string> _shipments = new();

    /// <param name="failShippingFor">
    /// Order ids that must fail fulfilment — the demo and tests use this to
    /// force the compensation path without a real warehouse.
    /// </param>
    public ShippingService(IEventBus bus, IEnumerable<Guid>? failShippingFor = null)
    {
        _bus = bus;
        _failShippingFor = failShippingFor?.ToHashSet() ?? new HashSet<Guid>();
        bus.Subscribe<PaymentSucceeded>(OnPaymentSucceeded);
    }

    public IReadOnlyDictionary<Guid, string> Shipments => _shipments;

    /// <summary>Fail fulfilment for an order that was previously set to succeed.</summary>
    public void FailShippingFor(Guid orderId) => _failShippingFor.Add(orderId);

    private async Task OnPaymentSucceeded(PaymentSucceeded e)
    {
        if (!MarkSeen(e))
        {
            return; // Redelivery: never ship twice for the same payment.
        }

        if (_failShippingFor.Contains(e.OrderId))
        {
            await _bus.PublishAsync(new ShipmentFailed(e.OrderId, "Item out of stock."));
            return;
        }

        var tracking = $"TRK-{e.OrderId:N}"[..12].ToUpperInvariant();
        _shipments[e.OrderId] = tracking;
        await _bus.PublishAsync(new ShipmentCreated(e.OrderId, tracking));
    }
}
