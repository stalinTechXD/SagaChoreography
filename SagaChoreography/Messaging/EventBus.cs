using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace SagaChoreography.Messaging;

// In choreography there is NO central controller. Services communicate purely through events
// published to a shared bus/topic; each service subscribes to the events it cares about.

public record OrderCreated(Guid OrderId, string Sku, int Quantity, decimal Amount, string Address);
public record InventoryReserved(Guid OrderId);
public record InventoryFailed(Guid OrderId, string Reason);
public record InventoryReleased(Guid OrderId);
public record PaymentCharged(Guid OrderId);
public record PaymentFailed(Guid OrderId, string Reason);
public record PaymentRefunded(Guid OrderId);
public record ShipmentCreated(Guid OrderId, string TrackingNumber);
public record ShippingFailed(Guid OrderId, string Reason);

/// <summary>
/// Minimal publish/subscribe event bus. In production this maps to an Azure Service Bus
/// Topic with a subscription (and SQL filter on event type) per consuming service.
/// </summary>
public interface IEventBus
{
    void Subscribe<TEvent>(Func<TEvent, Task> handler);
    Task PublishAsync<TEvent>(TEvent @event);
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

public sealed class InMemoryEventBus(ILogger<InMemoryEventBus> log) : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<object, Task>>> _handlers = new();

    public void Subscribe<TEvent>(Func<TEvent, Task> handler) =>
        _handlers.GetOrAdd(typeof(TEvent), _ => []).Add(e => handler((TEvent)e));

    public async Task PublishAsync<TEvent>(TEvent @event)
    {
        log.LogInformation("EVENT {Event}: {Payload}", typeof(TEvent).Name, JsonSerializer.Serialize(@event));
        if (!_handlers.TryGetValue(typeof(TEvent), out var list)) return;
        // Fire handlers independently, mimicking separate subscribers on a topic.
        foreach (var h in list.ToArray())
            _ = Task.Run(() => h(@event!));
        await Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

/// <summary>
/// Azure Service Bus Topic implementation. Publishes every event to a single topic with the
/// event type name as the message Subject; a per-process subscription receives them and the bus
/// dispatches to the handlers registered for that event type. The topic and subscription are
/// created automatically if missing.
/// </summary>
public sealed class ServiceBusTopicEventBus : IEventBus, IAsyncDisposable
{
    public const string TopicName = "saga-events";
    public const string SubscriptionName = "saga-app";

    private readonly ServiceBusClient _client;
    private readonly ServiceBusAdministrationClient _admin;
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusTopicEventBus> _log;
    private readonly ConcurrentDictionary<string, List<Func<string, Task>>> _handlers = new();
    private ServiceBusProcessor? _processor;

    public ServiceBusTopicEventBus(string connectionString, ILogger<ServiceBusTopicEventBus> log)
    {
        _client = new ServiceBusClient(connectionString);
        _admin = new ServiceBusAdministrationClient(connectionString);
        _sender = _client.CreateSender(TopicName);
        _log = log;
    }

    public void Subscribe<TEvent>(Func<TEvent, Task> handler) =>
        _handlers.GetOrAdd(typeof(TEvent).Name, _ => [])
                 .Add(body => handler(JsonSerializer.Deserialize<TEvent>(body)!));

    public Task PublishAsync<TEvent>(TEvent @event)
    {
        var msg = new ServiceBusMessage(JsonSerializer.Serialize(@event))
        {
            Subject = typeof(TEvent).Name,
            ContentType = "application/json"
        };
        _log.LogInformation("EVENT {Event}", typeof(TEvent).Name);
        return _sender.SendMessageAsync(msg);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await EnsureTopologyAsync(ct);
        _processor = _client.CreateProcessor(TopicName, SubscriptionName,
            new ServiceBusProcessorOptions { MaxConcurrentCalls = 4, MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5) });
        _processor.ProcessMessageAsync += async a =>
        {
            if (_handlers.TryGetValue(a.Message.Subject, out var handlers))
                foreach (var h in handlers.ToArray())
                    await h(a.Message.Body.ToString());
        };
        _processor.ProcessErrorAsync += a => { _log.LogError(a.Exception, "Service Bus topic error"); return Task.CompletedTask; };
        await _processor.StartProcessingAsync(ct);
    }

    private async Task EnsureTopologyAsync(CancellationToken ct)
    {
        if (!await _admin.TopicExistsAsync(TopicName, ct))
        {
            await _admin.CreateTopicAsync(TopicName, ct);
            _log.LogInformation("Created Service Bus topic {Topic}", TopicName);
        }
        if (!await _admin.SubscriptionExistsAsync(TopicName, SubscriptionName, ct))
        {
            await _admin.CreateSubscriptionAsync(TopicName, SubscriptionName, ct);
            _log.LogInformation("Created Service Bus subscription {Subscription}", SubscriptionName);
        }
    }

    public async Task StopAsync()
    {
        if (_processor is not null) await _processor.StopProcessingAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null) await _processor.DisposeAsync();
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}

/// <summary>Starts/stops the event bus after all services have registered their subscriptions.</summary>
public sealed class EventBusHost(IEventBus bus) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => bus.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => bus.StopAsync();
}
