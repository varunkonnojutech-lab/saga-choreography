namespace SagaChoreography.Core;

/// <summary>The states one order moves through during the saga.</summary>
public enum OrderStatus
{
    Placed,
    Paid,
    Shipped,
    Cancelled,
}

/// <summary>
/// The ordering service. It starts the saga by publishing
/// <see cref="OrderPlaced"/>, then reacts to what the other services report:
/// payment success moves the order to Paid, a shipment completes it, and any
/// failure (or the refund that follows one) cancels it.
/// </summary>
public sealed class OrderService : SagaParticipant
{
    private readonly IEventBus _bus;
    private readonly Dictionary<Guid, OrderStatus> _orders = new();

    public OrderService(IEventBus bus)
    {
        _bus = bus;
        bus.Subscribe<PaymentSucceeded>(OnPaymentSucceeded);
        bus.Subscribe<PaymentFailed>(OnPaymentFailed);
        bus.Subscribe<ShipmentCreated>(OnShipmentCreated);
        bus.Subscribe<PaymentRefunded>(OnPaymentRefunded);
    }

    public IReadOnlyDictionary<Guid, OrderStatus> Orders => _orders;

    /// <param name="orderId">
    /// Optional fixed id, so callers can pre-configure a failure for exactly
    /// this order (declined card, out-of-stock item) before placing it.
    /// </param>
    public async Task<Guid> PlaceOrderAsync(string customerEmail, decimal totalAmount,
        Guid? orderId = null, CancellationToken ct = default)
    {
        var id = orderId ?? Guid.NewGuid();
        _orders[id] = OrderStatus.Placed;
        await _bus.PublishAsync(new OrderPlaced(id, customerEmail, totalAmount), ct);
        return id;
    }

    private Task OnPaymentSucceeded(PaymentSucceeded e)
    {
        if (!MarkSeen(e) || !_orders.ContainsKey(e.OrderId))
        {
            return Task.CompletedTask;
        }

        _orders[e.OrderId] = OrderStatus.Paid;
        return Task.CompletedTask;
    }

    private Task OnPaymentFailed(PaymentFailed e)
    {
        if (!MarkSeen(e) || !_orders.ContainsKey(e.OrderId))
        {
            return Task.CompletedTask;
        }

        // Nothing to undo yet: no charge was taken, no shipment booked.
        _orders[e.OrderId] = OrderStatus.Cancelled;
        return Task.CompletedTask;
    }

    private Task OnShipmentCreated(ShipmentCreated e)
    {
        if (!MarkSeen(e) || !_orders.ContainsKey(e.OrderId))
        {
            return Task.CompletedTask;
        }

        _orders[e.OrderId] = OrderStatus.Shipped;
        return Task.CompletedTask;
    }

    private Task OnPaymentRefunded(PaymentRefunded e)
    {
        if (!MarkSeen(e) || !_orders.ContainsKey(e.OrderId))
        {
            return Task.CompletedTask;
        }

        // Compensation complete: the money is back, so the order is cancelled.
        _orders[e.OrderId] = OrderStatus.Cancelled;
        return Task.CompletedTask;
    }
}
