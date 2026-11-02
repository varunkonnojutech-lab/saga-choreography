using SagaChoreography.Core;

/// <summary>
/// Runs the saga four times: the happy path, a declined card, a warehouse
/// failure that unwinds via refund, and a redelivered event that must change
/// nothing. Watch the event log — it is the whole choreography.
/// </summary>
var bus = new InMemoryEventBus();
var orders = new OrderService(bus);
var payments = new PaymentService(bus);
var shipping = new ShippingService(bus);

void PrintTrace(string title, Guid orderId)
{
    Console.WriteLine($"\n=== {title} ===");
    foreach (var e in bus.Published.Where(e => e.OrderId == orderId))
    {
        Console.WriteLine($"  {e.GetType().Name,-18} order {e.OrderId.ToString()[..8]}");
    }

    Console.WriteLine($"  => Order status: {orders.Orders[orderId]}"
        + (payments.Charges.TryGetValue(orderId, out var charge) ? $", charge {charge}" : "")
        + (shipping.Shipments.TryGetValue(orderId, out var tracking) ? $", tracking {tracking}" : "")
        + (payments.RefundedOrders.Contains(orderId) ? ", REFUNDED" : ""));
}

// 1. Happy path: order -> charge -> ship -> complete.
var happy = await orders.PlaceOrderAsync("ada@example.com", 49.99m);
PrintTrace("1. Happy path", happy);

// 2. Payment failure: card declined, order cancelled, nothing ships.
var declinedId = Guid.NewGuid();
payments.FailPaymentFor(declinedId);
var declined = await orders.PlaceOrderAsync("grace@example.com", 19.99m, declinedId);
PrintTrace("2. Payment declined", declined);

// 3. Shipping failure: charged, then out of stock -> refund -> cancelled.
var stockoutId = Guid.NewGuid();
shipping.FailShippingFor(stockoutId);
var stockout = await orders.PlaceOrderAsync("linus@example.com", 129.00m, stockoutId);
PrintTrace("3. Shipping failed, payment refunded (compensation)", stockout);

// 4. Duplicate delivery: re-deliver the happy path's first two events.
//    Idempotent consumers ignore them — no double charge, no double shipment.
var firstEvents = bus.Published.Where(e => e.OrderId == happy).Take(2).ToList();
foreach (var e in firstEvents)
{
    await bus.RepublishAsync(e);
}

Console.WriteLine("\n=== 4. Duplicate events redelivered ===");
Console.WriteLine($"  Charges for happy order: {payments.Charges.Count(c => c.Key == happy)} (must be 1)");
Console.WriteLine($"  Shipments for happy order: {shipping.Shipments.Count(s => s.Key == happy)} (must be 1)");
Console.WriteLine($"  Order status: {orders.Orders[happy]} (must still be Shipped)");
