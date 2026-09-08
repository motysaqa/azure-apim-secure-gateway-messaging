using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Booking.Worker.Handling;

namespace Booking.Worker.Messaging;

/// <summary>
/// Receives from the Service Bus queue and applies the handler's decision.
///
/// Nothing but plumbing lives here: connect with the managed identity, hand the
/// body to <see cref="BookingCreatedHandler"/>, then complete, abandon or
/// dead-letter according to what it decided. Keeping the decision out of this
/// class is what makes the interesting behaviour testable.
///
/// Autocomplete is off. With it on, a message is settled before the handler has
/// finished, so a crash mid-write loses the booking silently.
/// </summary>
public sealed class ServiceBusConsumer : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServiceBusConsumer> _logger;

    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    public ServiceBusConsumer(IServiceProvider services, IConfiguration configuration,
        ILogger<ServiceBusConsumer> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fullyQualifiedNamespace = _configuration["ServiceBus:FullyQualifiedNamespace"];
        var queueName = _configuration["ServiceBus:QueueName"] ?? "booking-created";

        if (string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
        {
            // Running from a clone with nothing provisioned. Say so once and idle,
            // rather than crash-looping the container.
            _logger.LogWarning(
                "ServiceBus:FullyQualifiedNamespace is not configured. The worker is idle; " +
                "see docs/setup.md to provision a namespace, or run the unit tests, which " +
                "exercise the handler without a broker.");
            return;
        }

        _client = new ServiceBusClient(fullyQualifiedNamespace, new DefaultAzureCredential(),
            new ServiceBusClientOptions { TransportType = ServiceBusTransportType.AmqpWebSockets });

        _processor = _client.CreateProcessor(queueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = _configuration.GetValue("ServiceBus:MaxConcurrentCalls", 4),
            PrefetchCount = _configuration.GetValue("ServiceBus:PrefetchCount", 10),
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        });

        _processor.ProcessMessageAsync += OnMessage;
        _processor.ProcessErrorAsync += OnError;

        await _processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("Listening on {Namespace}/{Queue}", fullyQualifiedNamespace, queueName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        finally
        {
            await _processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task OnMessage(ProcessMessageEventArgs args)
    {
        // A scope per message: the handler and its store are resolved fresh, which
        // is what keeps concurrent deliveries from sharing mutable state.
        using var scope = _services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<BookingCreatedHandler>();

        var messageId = string.IsNullOrWhiteSpace(args.Message.MessageId)
            ? args.Message.SequenceNumber.ToString()
            : args.Message.MessageId;

        var body = args.Message.Body.ToString();
        var result = await handler.Handle(messageId, body, args.CancellationToken);

        switch (result.Outcome)
        {
            case HandlerOutcome.Processed:
            case HandlerOutcome.Duplicate:
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                break;

            case HandlerOutcome.DeadLetter:
                await args.DeadLetterMessageAsync(args.Message, result.Reason, result.Description,
                    args.CancellationToken);
                _logger.LogError("Dead-lettered {MessageId}: {Reason} - {Description}",
                    messageId, result.Reason, result.Description);
                break;

            case HandlerOutcome.Retry:
            default:
                // Abandon returns the lock immediately. After MaxDeliveryCount
                // attempts Service Bus dead-letters it automatically, which is the
                // safety net for a "transient" failure that turns out not to be.
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
                _logger.LogWarning("Abandoned {MessageId} for redelivery: {Description}",
                    messageId, result.Description);
                break;
        }
    }

    private Task OnError(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus error in {Operation} on {Entity}", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_processor is not null) await _processor.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
    }
}
