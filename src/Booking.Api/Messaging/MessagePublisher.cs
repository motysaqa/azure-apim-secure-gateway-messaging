using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Booking.Contracts;

namespace Booking.Api.Messaging;

public interface IMessagePublisher : IAsyncDisposable
{
    /// <summary>Human-readable destination, used in health output and logs.</summary>
    string Destination { get; }

    Task PublishAsync(string messageId, string payload, string correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes to an Azure Service Bus queue using the app's managed identity.
///
/// There is no connection string anywhere in this class or in configuration. The
/// namespace is a hostname, and <see cref="DefaultAzureCredential"/> resolves to
/// the App Service system-assigned identity in Azure and to the developer's
/// az-cli login locally. That is what removes the shared secret entirely, rather
/// than moving it into Key Vault and calling it solved.
/// </summary>
public sealed class ServiceBusMessagePublisher : IMessagePublisher
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusMessagePublisher> _logger;

    public ServiceBusMessagePublisher(IConfiguration configuration, ILogger<ServiceBusMessagePublisher> logger)
    {
        _logger = logger;

        var fullyQualifiedNamespace = configuration["ServiceBus:FullyQualifiedNamespace"]
            ?? throw new InvalidOperationException(
                "ServiceBus:FullyQualifiedNamespace is not configured (e.g. sb-booking-dev.servicebus.windows.net).");
        var queueName = configuration["ServiceBus:QueueName"] ?? "booking-created";

        _client = new ServiceBusClient(fullyQualifiedNamespace, new DefaultAzureCredential(),
            new ServiceBusClientOptions
            {
                TransportType = ServiceBusTransportType.AmqpWebSockets,
                RetryOptions = new ServiceBusRetryOptions
                {
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = 3,
                    Delay = TimeSpan.FromMilliseconds(500),
                    MaxDelay = TimeSpan.FromSeconds(10)
                }
            });

        _sender = _client.CreateSender(queueName);
        Destination = $"{fullyQualifiedNamespace}/{queueName}";
    }

    public string Destination { get; }

    public async Task PublishAsync(string messageId, string payload, string correlationId,
        CancellationToken cancellationToken)
    {
        var message = new ServiceBusMessage(payload)
        {
            // Service Bus duplicate detection keys on MessageId. Combined with the
            // worker's own dedupe table, a redelivery cannot create a second booking.
            MessageId = messageId,
            CorrelationId = correlationId,
            ContentType = "application/json",
            Subject = nameof(BookingCreated)
        };

        await _sender.SendMessageAsync(message, cancellationToken);
        _logger.LogInformation("Published {MessageId} to {Destination} (correlation {CorrelationId})",
            messageId, Destination, correlationId);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}

/// <summary>
/// Local-development publisher. Used when no Service Bus namespace is configured
/// so the API runs from a clone with nothing provisioned; it records what it
/// would have sent, which is enough for the integration tests.
/// </summary>
public sealed class InMemoryMessagePublisher : IMessagePublisher
{
    private readonly List<(string MessageId, string Payload, string CorrelationId)> _published = [];
    private readonly ILogger<InMemoryMessagePublisher> _logger;
    private readonly object _gate = new();

    public InMemoryMessagePublisher(ILogger<InMemoryMessagePublisher> logger) => _logger = logger;

    public string Destination => "in-memory (no Service Bus namespace configured)";

    public IReadOnlyList<(string MessageId, string Payload, string CorrelationId)> Published
    {
        get { lock (_gate) return _published.ToList(); }
    }

    public Task PublishAsync(string messageId, string payload, string correlationId,
        CancellationToken cancellationToken)
    {
        lock (_gate) _published.Add((messageId, payload, correlationId));
        _logger.LogInformation(
            "Service Bus is not configured; message {MessageId} was recorded in memory instead", messageId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
