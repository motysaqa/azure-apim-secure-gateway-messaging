using System.Data;
using System.Globalization;
using Booking.Contracts;
using Microsoft.Data.Sqlite;

namespace Booking.Worker.Storage;

/// <summary>
/// The worker's own store: confirmed bookings plus the deduplication ledger.
///
/// The ledger is what makes the handler idempotent. Service Bus guarantees
/// at-least-once delivery, and the API's outbox can publish the same message
/// twice, so "have I already handled this MessageId" has to be answered by
/// durable state, not by hoping it does not happen.
///
/// The insert into <c>processed_messages</c> and the insert into
/// <c>confirmed_bookings</c> share one transaction, so the ledger can never claim
/// a message was handled when the booking write was rolled back.
/// </summary>
public sealed class ProcessedBookingStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ProcessedBookingStore(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("WorkerStore")
                               ?? "Data Source=worker.db";
        EnsureDirectoryExists(connectionString);

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        Initialise();
    }

    /// <summary>
    /// SQLite will not create a missing directory, and the error it raises
    /// ("SQLite Error 14: unable to open database file") names neither the path
    /// nor the reason. This is not hypothetical: the App Service setting points
    /// at /home/data/bookings.db, and /home/data does not exist on a fresh app.
    /// </summary>
    private static void EnsureDirectoryExists(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.StartsWith(":memory:", StringComparison.Ordinal))
            return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    private void Initialise()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS processed_messages (
                message_id   TEXT PRIMARY KEY,
                booking_id   TEXT NOT NULL,
                processed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS confirmed_bookings (
                booking_id     TEXT PRIMARY KEY,
                property_id    TEXT NOT NULL,
                guest_email    TEXT NOT NULL,
                check_in       TEXT NOT NULL,
                check_out      TEXT NOT NULL,
                nights         INTEGER NOT NULL,
                guests         INTEGER NOT NULL,
                total_amount   TEXT NOT NULL,
                currency       TEXT NOT NULL,
                confirmed_at   TEXT NOT NULL,
                correlation_id TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS dead_letters (
                message_id   TEXT PRIMARY KEY,
                reason       TEXT NOT NULL,
                description  TEXT NOT NULL,
                body         TEXT NOT NULL,
                recorded_at  TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<bool> AlreadyProcessed(string messageId, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM processed_messages WHERE message_id = $id;";
        command.Parameters.AddWithValue("$id", messageId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Records the booking and marks the message handled, atomically.
    /// Returns false when the message had already been recorded, which is the
    /// race two concurrent deliveries of the same message would otherwise win.
    /// </summary>
    public async Task<bool> TryRecord(BookingCreated message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            await using (var ledger = _connection.CreateCommand())
            {
                ledger.Transaction = transaction;
                // ON CONFLICT DO NOTHING turns the duplicate check into a single
                // atomic operation rather than a check-then-act race.
                ledger.CommandText =
                    """
                    INSERT INTO processed_messages (message_id, booking_id, processed_at)
                    VALUES ($messageId, $bookingId, $now)
                    ON CONFLICT(message_id) DO NOTHING;
                    """;
                ledger.Parameters.AddWithValue("$messageId", message.MessageId);
                ledger.Parameters.AddWithValue("$bookingId", message.BookingId);
                ledger.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

                if (await ledger.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            await using (var booking = _connection.CreateCommand())
            {
                booking.Transaction = transaction;
                // A booking may legitimately be re-confirmed with corrected details
                // under a new MessageId, so upsert rather than insert.
                booking.CommandText =
                    """
                    INSERT INTO confirmed_bookings (booking_id, property_id, guest_email, check_in,
                                                    check_out, nights, guests, total_amount, currency,
                                                    confirmed_at, correlation_id)
                    VALUES ($bookingId, $propertyId, $guestEmail, $checkIn,
                            $checkOut, $nights, $guests, $totalAmount, $currency,
                            $confirmedAt, $correlationId)
                    ON CONFLICT(booking_id) DO UPDATE SET
                        property_id  = excluded.property_id,
                        guest_email  = excluded.guest_email,
                        check_in     = excluded.check_in,
                        check_out    = excluded.check_out,
                        nights       = excluded.nights,
                        guests       = excluded.guests,
                        total_amount = excluded.total_amount,
                        currency     = excluded.currency,
                        confirmed_at = excluded.confirmed_at;
                    """;
                booking.Parameters.AddWithValue("$bookingId", message.BookingId);
                booking.Parameters.AddWithValue("$propertyId", message.PropertyId);
                booking.Parameters.AddWithValue("$guestEmail", message.GuestEmail);
                booking.Parameters.AddWithValue("$checkIn", message.CheckIn.ToString("O"));
                booking.Parameters.AddWithValue("$checkOut", message.CheckOut.ToString("O"));
                booking.Parameters.AddWithValue("$nights", message.Nights);
                booking.Parameters.AddWithValue("$guests", message.Guests);
                booking.Parameters.AddWithValue("$totalAmount",
                    message.TotalAmount.ToString(CultureInfo.InvariantCulture));
                booking.Parameters.AddWithValue("$currency", message.Currency);
                booking.Parameters.AddWithValue("$confirmedAt", DateTimeOffset.UtcNow.ToString("O"));
                booking.Parameters.AddWithValue("$correlationId", message.CorrelationId);
                await booking.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordDeadLetter(string messageId, string reason, string description, string body,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO dead_letters (message_id, reason, description, body, recorded_at)
                VALUES ($messageId, $reason, $description, $body, $now)
                ON CONFLICT(message_id) DO UPDATE SET
                    reason = excluded.reason,
                    description = excluded.description,
                    recorded_at = excluded.recorded_at;
                """;
            command.Parameters.AddWithValue("$messageId", messageId);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$body", body.Length > 4000 ? body[..4000] : body);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> CountConfirmed(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM confirmed_bookings;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public sealed record ConfirmedBooking(string BookingId, string PropertyId, int Nights,
        decimal TotalAmount, string Currency, string CorrelationId);

    public async Task<IReadOnlyList<ConfirmedBooking>> ListConfirmed(int limit, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT booking_id, property_id, nights, total_amount, currency, correlation_id
            FROM confirmed_bookings
            ORDER BY confirmed_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<ConfirmedBooking>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ConfirmedBooking(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetString(4), reader.GetString(5)));
        }
        return results;
    }

    public void Dispose()
    {
        _connection.Dispose();
        _writeLock.Dispose();
    }
}
