namespace SagaChoreography.Core;

/// <summary>
/// The transport every service publishes to and subscribes from. In a real
/// system this is a broker (RabbitMQ, Kafka, Azure Service Bus); here it is
/// synchronous and in-memory so the whole saga runs in one process and the
/// event flow is easy to trace. Delivery is at-least-once: <see cref="Republish"/>
/// deliberately re-delivers an event, and services must cope.
/// </summary>
public interface IEventBus
{
    void Subscribe<T>(Func<T, Task> handler) where T : SagaEvent;
    Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : SagaEvent;
}

/// <summary>
/// In-memory bus. Handlers run synchronously in subscription order, so
/// publishing <see cref="OrderPlaced"/> runs the entire happy path
/// (charge → ship → complete) before <c>PublishAsync</c> returns.
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly Dictionary<Type, List<Func<SagaEvent, Task>>> _handlers = new();

    /// <summary>Every event ever published, in order — the saga's audit trail.</summary>
    public List<SagaEvent> Published { get; } = new();

    public void Subscribe<T>(Func<T, Task> handler) where T : SagaEvent
    {
        if (!_handlers.TryGetValue(typeof(T), out var list))
        {
            list = new List<Func<SagaEvent, Task>>();
            _handlers[typeof(T)] = list;
        }

        list.Add(e => handler((T)e));
    }

    public async Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : SagaEvent
    {
        Published.Add(@event);
        if (_handlers.TryGetValue(typeof(T), out var handlers))
        {
            foreach (var handler in handlers)
            {
                await handler(@event);
            }
        }
    }

    /// <summary>Re-delivers an already-published event, as a real broker does on redelivery.</summary>
    public Task RepublishAsync(SagaEvent @event, CancellationToken ct = default) =>
        @event switch
        {
            OrderPlaced e => PublishAsync(e, ct),
            PaymentSucceeded e => PublishAsync(e, ct),
            PaymentFailed e => PublishAsync(e, ct),
            ShipmentCreated e => PublishAsync(e, ct),
            ShipmentFailed e => PublishAsync(e, ct),
            PaymentRefunded e => PublishAsync(e, ct),
            _ => throw new ArgumentException($"Unknown event type {@event.GetType().Name}.", nameof(@event)),
        };
}

/// <summary>
/// Shared base for saga participants. Each service tracks the event ids it
/// has already processed, so a redelivered event is a no-op instead of a
/// double charge, a double shipment, or a double refund.
/// </summary>
public abstract class SagaParticipant
{
    private readonly HashSet<Guid> _seenEventIds = new();

    /// <summary>Returns false when this event was already handled — skip it.</summary>
    protected bool MarkSeen(SagaEvent @event) => _seenEventIds.Add(@event.EventId);
}
