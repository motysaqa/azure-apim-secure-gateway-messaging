using Booking.Api.Storage;

namespace Booking.Api.Messaging;

/// <summary>
/// Drains the outbox to Service Bus.
///
/// This runs outside the request so a Service Bus outage slows delivery instead
/// of failing bookings. The trade-off is deliberate and worth stating: the outbox
/// gives at-least-once delivery, never exactly-once. The message can be published
/// and the row's published_at update can still be lost, so the same message may
/// be sent twice. That is precisely why the worker deduplicates on MessageId -
/// the two halves are one design, not two independent safety nets.
/// </summary>
public sealed class OutboxPublisher : BackgroundService
{
    private readonly BookingStore _store;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<OutboxPublisher> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;
    private readonly int _maxAttempts;

    public OutboxPublisher(BookingStore store, IMessagePublisher publisher,
        IConfiguration configuration, ILogger<OutboxPublisher> logger)
    {
        _store = store;
        _publisher = publisher;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(
            configuration.GetValue("Outbox:PollIntervalSeconds", 2));
        _batchSize = configuration.GetValue("Outbox:BatchSize", 20);
        _maxAttempts = configuration.GetValue("Outbox:MaxAttempts", 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox publisher started; destination is {Destination}",
            _publisher.Destination);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnce(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die: a dead publisher is a silent outage.
                _logger.LogError(ex, "Outbox drain failed; retrying after the poll interval");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Outbox publisher stopping");
    }

    internal async Task<int> DrainOnce(CancellationToken cancellationToken)
    {
        var pending = await _store.ReadPending(_batchSize, cancellationToken);
        var published = 0;

        foreach (var entry in pending)
        {
            if (entry.Attempts >= _maxAttempts)
            {
                // Left in place on purpose. Deleting it would hide the problem, and
                // reconcile_bookings.py reports exactly this as a mismatch.
                _logger.LogError(
                    "Outbox entry {OutboxId} for booking {BookingId} has failed {Attempts} times and is being left for manual triage",
                    entry.Id, entry.BookingId, entry.Attempts);
                continue;
            }

            try
            {
                await _publisher.PublishAsync(entry.MessageId, entry.Payload, entry.CorrelationId, cancellationToken);
                await _store.MarkPublished(entry.Id, cancellationToken);
                published++;
            }
            catch (Exception ex)
            {
                await _store.MarkFailed(entry.Id, ex.Message, cancellationToken);
                _logger.LogWarning(ex,
                    "Failed to publish outbox entry {OutboxId} for booking {BookingId} (attempt {Attempt})",
                    entry.Id, entry.BookingId, entry.Attempts + 1);
            }
        }

        if (published > 0)
            _logger.LogInformation("Published {Count} outbox message(s)", published);

        return published;
    }
}
