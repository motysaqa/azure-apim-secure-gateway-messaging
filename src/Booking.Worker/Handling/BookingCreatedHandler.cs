using System.Text.Json;
using Booking.Contracts;
using Booking.Worker.Storage;

namespace Booking.Worker.Handling;

public enum HandlerOutcome
{
    /// <summary>Recorded for the first time. Complete the message.</summary>
    Processed,

    /// <summary>Already handled. Complete the message; doing nothing is the correct action.</summary>
    Duplicate,

    /// <summary>Will never succeed no matter how often it is retried. Dead-letter it.</summary>
    DeadLetter,

    /// <summary>Failed for a reason that may not recur. Abandon and let Service Bus redeliver.</summary>
    Retry
}

public sealed record HandlerResult(HandlerOutcome Outcome, string Reason = "", string Description = "")
{
    public static readonly HandlerResult Processed = new(HandlerOutcome.Processed);
    public static readonly HandlerResult Duplicate = new(HandlerOutcome.Duplicate);

    public static HandlerResult Poison(string reason, string description) =>
        new(HandlerOutcome.DeadLetter, reason, description);

    public static HandlerResult Transient(string description) =>
        new(HandlerOutcome.Retry, "TransientFailure", description);
}

/// <summary>
/// Handles one <see cref="BookingCreated"/> message.
///
/// Deliberately knows nothing about Service Bus: it takes a string body and
/// returns a decision. That is what lets every branch below - malformed JSON, an
/// unknown schema version, a duplicate, a business-invalid booking - be tested
/// without a broker.
///
/// The central distinction is between a message that will *never* succeed and one
/// that merely failed *this time*. Retrying poison forever is how a queue backs
/// up; dead-lettering a transient failure is how a booking gets lost.
/// </summary>
public sealed class BookingCreatedHandler(
    ProcessedBookingStore store,
    ILogger<BookingCreatedHandler> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int SupportedVersion = 1;

    public async Task<HandlerResult> Handle(string messageId, string body, CancellationToken cancellationToken)
    {
        BookingCreated? message;
        try
        {
            message = JsonSerializer.Deserialize<BookingCreated>(body, Json);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Message {MessageId} is not valid JSON and will be dead-lettered", messageId);
            await store.RecordDeadLetter(messageId, "MalformedJson", ex.Message, body, cancellationToken);
            return HandlerResult.Poison("MalformedJson", Truncate(ex.Message));
        }

        if (message is null)
        {
            await store.RecordDeadLetter(messageId, "EmptyBody", "Body deserialised to null", body, cancellationToken);
            return HandlerResult.Poison("EmptyBody", "Body deserialised to null");
        }

        if (message.Version != SupportedVersion)
        {
            // A newer producer than this worker. Dead-letter rather than guess:
            // the message is preserved and can be replayed after a deploy.
            var description = $"Schema version {message.Version} is not supported (this worker handles v{SupportedVersion})";
            logger.LogError("Message {MessageId}: {Description}", messageId, description);
            await store.RecordDeadLetter(messageId, "UnsupportedVersion", description, body, cancellationToken);
            return HandlerResult.Poison("UnsupportedVersion", description);
        }

        var businessErrors = BookingValidator.Validate(new CreateBookingRequest
        {
            PropertyId = message.PropertyId,
            GuestEmail = message.GuestEmail,
            CheckIn = message.CheckIn,
            CheckOut = message.CheckOut,
            Guests = message.Guests,
            TotalAmount = message.TotalAmount,
            Currency = message.Currency
            // The past-date rule is skipped on the consuming side: a message that
            // sat in the queue over a weekend is late, not invalid.
        }, DateOnly.MinValue);

        if (businessErrors.Count > 0)
        {
            var description = string.Join("; ",
                businessErrors.Select(kvp => $"{kvp.Key}: {string.Join(" ", kvp.Value)}"));
            logger.LogError("Message {MessageId} failed validation and will be dead-lettered: {Description}",
                messageId, description);
            await store.RecordDeadLetter(messageId, "ValidationFailed", description, body, cancellationToken);
            return HandlerResult.Poison("ValidationFailed", Truncate(description));
        }

        try
        {
            var recorded = await store.TryRecord(message, cancellationToken);
            if (!recorded)
            {
                logger.LogInformation(
                    "Message {MessageId} for booking {BookingId} was already processed; completing without a second write",
                    messageId, message.BookingId);
                return HandlerResult.Duplicate;
            }

            logger.LogInformation(
                "Confirmed booking {BookingId} for property {PropertyId}, {Nights} night(s), {TotalAmount} {Currency} (correlation {CorrelationId})",
                message.BookingId, message.PropertyId, message.Nights,
                message.TotalAmount, message.Currency, message.CorrelationId);

            return HandlerResult.Processed;
        }
        catch (Exception ex)
        {
            // A database write failure is the archetypal transient error: the disk
            // is full, the connection dropped. Abandon so Service Bus redelivers.
            logger.LogError(ex, "Storing booking {BookingId} failed; the message will be retried",
                message.BookingId);
            return HandlerResult.Transient(Truncate(ex.Message));
        }
    }

    private static string Truncate(string value) => value.Length > 250 ? value[..250] : value;
}
