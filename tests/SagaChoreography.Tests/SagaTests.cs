using SagaChoreography.Core;

namespace SagaChoreography.Tests;

/// <summary>
/// One test per saga path. Each test wires the three services to a fresh
/// bus, so the choreography — not any shared state — is what is tested.
/// </summary>
public sealed class SagaTests
{
    private static (InMemoryEventBus Bus, OrderService Orders, PaymentService Payments,
        ShippingService Shipping) Setup()
    {
        var bus = new InMemoryEventBus();
        var orders = new OrderService(bus);
        var payments = new PaymentService(bus);
        var shipping = new ShippingService(bus);
        return (bus, orders, payments, shipping);
    }

    [Fact]
    public async Task HappyPath_OrderShipsAndCompletes()
    {
        var (bus, orders, payments, shipping) = Setup();

        var orderId = await orders.PlaceOrderAsync("ada@example.com", 49.99m);

        Assert.Equal(OrderStatus.Shipped, orders.Orders[orderId]);
        Assert.Single(payments.Charges);
        Assert.Single(shipping.Shipments);
        Assert.Equal(
            new[] { "OrderPlaced", "PaymentSucceeded", "ShipmentCreated" },
            bus.Published.Select(e => e.GetType().Name));
    }

    [Fact]
    public async Task PaymentFailure_CancelsOrderWithoutShippingOrCharge()
    {
        var (_, orders, payments, shipping) = Setup();
        var orderId = Guid.NewGuid();
        payments.FailPaymentFor(orderId);

        await orders.PlaceOrderAsync("grace@example.com", 19.99m, orderId);

        Assert.Equal(OrderStatus.Cancelled, orders.Orders[orderId]);
        Assert.Empty(payments.Charges);
        Assert.Empty(shipping.Shipments);
    }

    [Fact]
    public async Task ShippingFailure_RefundsPaymentAndCancelsOrder()
    {
        var (bus, orders, payments, shipping) = Setup();
        var orderId = Guid.NewGuid();
        shipping.FailShippingFor(orderId);

        await orders.PlaceOrderAsync("linus@example.com", 129.00m, orderId);

        // Compensation ran: charged, then refunded, then cancelled.
        Assert.Equal(OrderStatus.Cancelled, orders.Orders[orderId]);
        Assert.Single(payments.Charges);
        Assert.Contains(orderId, payments.RefundedOrders);
        Assert.Empty(shipping.Shipments);
        Assert.Equal(
            new[] { "OrderPlaced", "PaymentSucceeded", "ShipmentFailed", "PaymentRefunded" },
            bus.Published.Select(e => e.GetType().Name));
    }

    [Fact]
    public async Task DuplicateEventDelivery_IsIdempotent()
    {
        var (bus, orders, payments, shipping) = Setup();
        var orderId = await orders.PlaceOrderAsync("ada@example.com", 49.99m);

        // Redeliver every event of this saga, as a broker would on retry.
        foreach (var e in bus.Published.Where(e => e.OrderId == orderId).ToList())
        {
            await bus.RepublishAsync(e);
        }

        Assert.Equal(OrderStatus.Shipped, orders.Orders[orderId]);
        Assert.Single(payments.Charges);
        Assert.Single(shipping.Shipments);
        Assert.DoesNotContain(orderId, payments.RefundedOrders);
    }

    [Fact]
    public async Task EventsForUnknownOrder_AreIgnored()
    {
        var (bus, orders, payments, shipping) = Setup();
        var ghostId = Guid.NewGuid();

        // A stray PaymentFailed for an order nobody placed: no crash and no
        // state created for it. (A PaymentSucceeded, by contrast, is a real
        // instruction to ship — the shipping service acts on it, which is
        // why payment events must always carry a valid correlation id.)
        await bus.PublishAsync(new PaymentFailed(ghostId, "Card declined."));

        Assert.Empty(orders.Orders);
        Assert.Empty(shipping.Shipments);
        Assert.Empty(payments.Charges);
    }
}
