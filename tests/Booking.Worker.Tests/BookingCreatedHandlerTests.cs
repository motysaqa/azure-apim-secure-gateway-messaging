using System.Text.Json;
using Booking.Contracts;
using Booking.Worker.Handling;
using Booking.Worker.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Booking.Worker.Tests;

/// <summary>
/// The handler decides completely on its own what to do with a message, so every
/// case below runs with no broker: a real SQLite store, a real handler, and a
/// string body. That is the whole reason the Service Bus plumbing was kept out
/// of <see cref="BookingCreatedHandler"/>.
/// </summary>
public sealed class BookingCreatedHandlerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"booking-worker-tests-{Guid.NewGuid():N}.db");
    private readonly ProcessedBookingStore _store;
    private readonly BookingCreatedHandler _handler;

    public BookingCreatedHandlerTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WorkerStore"] = $"Data Source={_databasePath}"
            })
            .Build();

        _store = new ProcessedBookingStore(configuration);
        _handler = new BookingCreatedHandler(_store, NullLogger<BookingCreatedHandler>.Instance);
    }

    private static BookingCreated Message(string? messageId = null, string bookingId = "BK-0001") => new()
    {
        Version = 1,
        MessageId = messageId ?? Guid.NewGuid().ToString(),
        BookingId = bookingId,
        PropertyId = "PROP-1001",
        GuestEmail = "guest@example.com",
        CheckIn = new DateOnly(2026, 6, 1),
        CheckOut = new DateOnly(2026, 6, 5),
        Guests = 2,
        TotalAmount = 592.40m,
        Currency = "EUR",
        OccurredAt = DateTimeOffset.UtcNow,
        CorrelationId = Guid.NewGuid().ToString()
    };

    private static string Body(BookingCreated message) => JsonSerializer.Serialize(message, Json);

    [Fact]
    public async Task A_valid_message_is_processed_and_stored()
    {
        var message = Message();

        var result = await _handler.Handle(message.MessageId, Body(message), CancellationToken.None);

        Assert.Equal(HandlerOutcome.Processed, result.Outcome);

        var confirmed = await _store.ListConfirmed(10, CancellationToken.None);
        var stored = Assert.Single(confirmed);
        Assert.Equal("BK-0001", stored.BookingId);
        Assert.Equal(4, stored.Nights);
        Assert.Equal(592.40m, stored.TotalAmount);
        Assert.Equal(message.CorrelationId, stored.CorrelationId);
    }

    [Fact]
    public async Task Redelivering_the_same_message_writes_nothing_a_second_time()
    {
        var message = Message();
        var body = Body(message);

        var first = await _handler.Handle(message.MessageId, body, CancellationToken.None);
        var second = await _handler.Handle(message.MessageId, body, CancellationToken.None);
        var third = await _handler.Handle(message.MessageId, body, CancellationToken.None);

        Assert.Equal(HandlerOutcome.Processed, first.Outcome);
        // A duplicate is completed, not retried and not dead-lettered: it has
        // already had its effect, so doing nothing is the correct action.
        Assert.Equal(HandlerOutcome.Duplicate, second.Outcome);
        Assert.Equal(HandlerOutcome.Duplicate, third.Outcome);

        Assert.Equal(1, await _store.CountConfirmed(CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_deliveries_of_the_same_message_produce_exactly_one_write()
    {
        var message = Message();
        var body = Body(message);

        // The check-then-act race: without the atomic ON CONFLICT insert in the
        // ledger, two deliveries can both see "not processed" and both write.
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _handler.Handle(message.MessageId, body, CancellationToken.None)));

        Assert.Equal(1, results.Count(r => r.Outcome == HandlerOutcome.Processed));
        Assert.Equal(7, results.Count(r => r.Outcome == HandlerOutcome.Duplicate));
        Assert.Equal(1, await _store.CountConfirmed(CancellationToken.None));
    }

    [Fact]
    public async Task A_different_message_for_the_same_booking_updates_it_without_duplicating()
    {
        var first = Message(bookingId: "BK-0002");
        var corrected = Message(bookingId: "BK-0002") with { Guests = 4, TotalAmount = 700.00m };

        await _handler.Handle(first.MessageId, Body(first), CancellationToken.None);
        var result = await _handler.Handle(corrected.MessageId, Body(corrected), CancellationToken.None);

        Assert.Equal(HandlerOutcome.Processed, result.Outcome);
        // One booking, updated - deduplication is on the message, not the booking.
        Assert.Equal(1, await _store.CountConfirmed(CancellationToken.None));

        var stored = Assert.Single(await _store.ListConfirmed(10, CancellationToken.None));
        Assert.Equal(700.00m, stored.TotalAmount);
    }

    [Fact]
    public async Task Malformed_json_is_dead_lettered_rather_than_retried_forever()
    {
        var result = await _handler.Handle("msg-broken", "{ this is not json", CancellationToken.None);

        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);
        Assert.Equal("MalformedJson", result.Reason);
        Assert.Equal(0, await _store.CountConfirmed(CancellationToken.None));
    }

    [Fact]
    public async Task A_future_schema_version_is_dead_lettered_rather_than_guessed_at()
    {
        var message = Message() with { Version = 99 };

        var result = await _handler.Handle(message.MessageId, Body(message), CancellationToken.None);

        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);
        Assert.Equal("UnsupportedVersion", result.Reason);
        // The message is preserved so it can be replayed after the worker is
        // upgraded, rather than being silently dropped.
        Assert.Contains("99", result.Description);
    }

    [Theory]
    [InlineData("guests", 0)]
    [InlineData("guests", 999)]
    public async Task A_business_invalid_message_is_dead_lettered(string expectedField, int guests)
    {
        var message = Message() with { Guests = guests };

        var result = await _handler.Handle(message.MessageId, Body(message), CancellationToken.None);

        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);
        Assert.Equal("ValidationFailed", result.Reason);
        Assert.Contains(expectedField, result.Description);
    }

    [Fact]
    public async Task A_message_that_sat_in_the_queue_past_its_check_in_date_is_still_processed()
    {
        // Late is not invalid. The API rejects past dates at the edge; the worker
        // must not, or a weekend-long outage turns every queued booking into a
        // dead letter.
        var message = Message() with
        {
            CheckIn = new DateOnly(2020, 1, 1),
            CheckOut = new DateOnly(2020, 1, 5)
        };

        var result = await _handler.Handle(message.MessageId, Body(message), CancellationToken.None);

        Assert.Equal(HandlerOutcome.Processed, result.Outcome);
    }

    [Fact]
    public async Task An_empty_body_is_dead_lettered()
    {
        var result = await _handler.Handle("msg-empty", "null", CancellationToken.None);

        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);
        Assert.Equal("EmptyBody", result.Reason);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(_databasePath + suffix)) File.Delete(_databasePath + suffix);
        }
        catch (IOException)
        {
            // Not worth failing a test run over a locked temp file.
        }
    }
}
