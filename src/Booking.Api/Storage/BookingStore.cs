using System.Data;
using System.Globalization;
using System.Text.Json;
using Booking.Contracts;
using Microsoft.Data.Sqlite;

namespace Booking.Api.Storage;

/// <summary>
/// Booking storage plus the transactional outbox.
///
/// The whole point of this class is the single transaction in
/// <see cref="TryCreate"/>: the booking row and the outbox row commit together or
/// not at all. Publishing to Service Bus inside the request would reintroduce the
/// failure it is designed to prevent - a message sent for a booking that was
/// rolled back, or a booking saved whose message was never sent.
///
/// SQLite stands in for SQL Server here so the repository runs from a clone. One
/// connection is held open and writes are serialised, which is fine at this scale
/// and is called out in the README as the first thing to replace.
/// </summary>
public sealed class BookingStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<BookingStore> _logger;

    public BookingStore(IConfiguration configuration, ILogger<BookingStore> logger)
    {
        _logger = logger;
        var connectionString = configuration.GetConnectionString("Bookings")
                               ?? "Data Source=bookings.db";

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
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS bookings (
                booking_id      TEXT PRIMARY KEY,
                idempotency_key TEXT NULL,
                property_id     TEXT NOT NULL,
                guest_email     TEXT NOT NULL,
                check_in        TEXT NOT NULL,
                check_out       TEXT NOT NULL,
                guests          INTEGER NOT NULL,
                -- Money is stored as text, never as REAL: SQLite's REAL is a
                -- double and 148.50 does not survive a round trip intact.
                total_amount    TEXT NOT NULL,
                currency        TEXT NOT NULL,
                status          TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                correlation_id  TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_bookings_idempotency
                ON bookings(idempotency_key) WHERE idempotency_key IS NOT NULL;

            CREATE TABLE IF NOT EXISTS outbox (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id   TEXT NOT NULL UNIQUE,
                booking_id   TEXT NOT NULL REFERENCES bookings(booking_id),
                payload      TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                published_at TEXT NULL,
                attempts     INTEGER NOT NULL DEFAULT 0,
                last_error   TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_outbox_pending
                ON outbox(id) WHERE published_at IS NULL;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates a booking and its outbox message in one transaction.
    /// Returns the existing booking, and <c>false</c>, when the idempotency key
    /// has been seen before.
    /// </summary>
    public async Task<(BookingResponse Booking, bool Created)> TryCreate(
        CreateBookingRequest request,
        string? idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (idempotencyKey is not null &&
                await FindByIdempotencyKey(idempotencyKey, cancellationToken) is { } existing)
            {
                _logger.LogInformation(
                    "Idempotency key {IdempotencyKey} already produced booking {BookingId}; returning it unchanged",
                    idempotencyKey, existing.BookingId);
                return (existing, false);
            }

            var bookingId = $"BK-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
            var createdAt = DateTimeOffset.UtcNow;

            var response = new BookingResponse
            {
                BookingId = bookingId,
                PropertyId = request.PropertyId!,
                GuestEmail = request.GuestEmail!,
                CheckIn = request.CheckIn!.Value,
                CheckOut = request.CheckOut!.Value,
                Nights = request.CheckOut.Value.DayNumber - request.CheckIn.Value.DayNumber,
                Guests = request.Guests!.Value,
                TotalAmount = request.TotalAmount!.Value,
                Currency = request.Currency!.ToUpperInvariant(),
                Status = "PENDING",
                CreatedAt = createdAt,
                CorrelationId = correlationId
            };

            var message = new BookingCreated
            {
                MessageId = Guid.NewGuid().ToString(),
                BookingId = response.BookingId,
                PropertyId = response.PropertyId,
                GuestEmail = response.GuestEmail,
                CheckIn = response.CheckIn,
                CheckOut = response.CheckOut,
                Guests = response.Guests,
                TotalAmount = response.TotalAmount,
                Currency = response.Currency,
                OccurredAt = createdAt,
                CorrelationId = correlationId
            };

            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            await using (var insertBooking = _connection.CreateCommand())
            {
                insertBooking.Transaction = transaction;
                insertBooking.CommandText =
                    """
                    INSERT INTO bookings (booking_id, idempotency_key, property_id, guest_email,
                                          check_in, check_out, guests, total_amount, currency,
                                          status, created_at, correlation_id)
                    VALUES ($bookingId, $idempotencyKey, $propertyId, $guestEmail,
                            $checkIn, $checkOut, $guests, $totalAmount, $currency,
                            $status, $createdAt, $correlationId);
                    """;
                insertBooking.Parameters.AddWithValue("$bookingId", response.BookingId);
                insertBooking.Parameters.AddWithValue("$idempotencyKey",
                    (object?)idempotencyKey ?? DBNull.Value);
                insertBooking.Parameters.AddWithValue("$propertyId", response.PropertyId);
                insertBooking.Parameters.AddWithValue("$guestEmail", response.GuestEmail);
                insertBooking.Parameters.AddWithValue("$checkIn", response.CheckIn.ToString("O"));
                insertBooking.Parameters.AddWithValue("$checkOut", response.CheckOut.ToString("O"));
                insertBooking.Parameters.AddWithValue("$guests", response.Guests);
                insertBooking.Parameters.AddWithValue("$totalAmount",
                    response.TotalAmount.ToString(CultureInfo.InvariantCulture));
                insertBooking.Parameters.AddWithValue("$currency", response.Currency);
                insertBooking.Parameters.AddWithValue("$status", response.Status);
                insertBooking.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
                insertBooking.Parameters.AddWithValue("$correlationId", correlationId);
                await insertBooking.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertOutbox = _connection.CreateCommand())
            {
                insertOutbox.Transaction = transaction;
                insertOutbox.CommandText =
                    """
                    INSERT INTO outbox (message_id, booking_id, payload, correlation_id, created_at)
                    VALUES ($messageId, $bookingId, $payload, $correlationId, $createdAt);
                    """;
                insertOutbox.Parameters.AddWithValue("$messageId", message.MessageId);
                insertOutbox.Parameters.AddWithValue("$bookingId", message.BookingId);
                insertOutbox.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(message, Json));
                insertOutbox.Parameters.AddWithValue("$correlationId", correlationId);
                insertOutbox.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
                await insertOutbox.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Created booking {BookingId} for property {PropertyId} with outbox message {MessageId} (correlation {CorrelationId})",
                response.BookingId, response.PropertyId, message.MessageId, correlationId);

            return (response, true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<BookingResponse?> Find(string bookingId, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM bookings WHERE booking_id = $id;";
        command.Parameters.AddWithValue("$id", bookingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private async Task<BookingResponse?> FindByIdempotencyKey(string key, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM bookings WHERE idempotency_key = $key;";
        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    // ---- Outbox -----------------------------------------------------------

    public sealed record OutboxEntry(
        long Id, string MessageId, string BookingId, string Payload, string CorrelationId, int Attempts);

    public async Task<IReadOnlyList<OutboxEntry>> ReadPending(int batchSize, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, message_id, booking_id, payload, correlation_id, attempts
            FROM outbox
            WHERE published_at IS NULL
            ORDER BY id
            LIMIT $batchSize;
            """;
        command.Parameters.AddWithValue("$batchSize", batchSize);

        var entries = new List<OutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new OutboxEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
        }
        return entries;
    }

    public async Task MarkPublished(long id, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE outbox SET published_at = $now, last_error = NULL WHERE id = $id;
                UPDATE bookings SET status = 'CONFIRMED'
                WHERE booking_id = (SELECT booking_id FROM outbox WHERE id = $id);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task MarkFailed(long id, string error, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE outbox
                SET attempts = attempts + 1,
                    last_error = $error
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            // Truncated: a stack trace in a database column helps nobody and the
            // full detail is already in the structured log.
            command.Parameters.AddWithValue("$error", error.Length > 500 ? error[..500] : error);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<(int Total, int Pending)> OutboxDepth(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), COALESCE(SUM(CASE WHEN published_at IS NULL THEN 1 ELSE 0 END), 0) FROM outbox;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt32(0), reader.GetInt32(1))
            : (0, 0);
    }

    public async Task<IReadOnlyList<BookingResponse>> ListBookings(int limit, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM bookings ORDER BY created_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var bookings = new List<BookingResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            bookings.Add(Map(reader));
        return bookings;
    }

    private static BookingResponse Map(SqliteDataReader reader)
    {
        var checkIn = DateOnly.Parse(reader.GetString(reader.GetOrdinal("check_in")), CultureInfo.InvariantCulture);
        var checkOut = DateOnly.Parse(reader.GetString(reader.GetOrdinal("check_out")), CultureInfo.InvariantCulture);

        return new BookingResponse
        {
            BookingId = reader.GetString(reader.GetOrdinal("booking_id")),
            PropertyId = reader.GetString(reader.GetOrdinal("property_id")),
            GuestEmail = reader.GetString(reader.GetOrdinal("guest_email")),
            CheckIn = checkIn,
            CheckOut = checkOut,
            Nights = checkOut.DayNumber - checkIn.DayNumber,
            Guests = reader.GetInt32(reader.GetOrdinal("guests")),
            TotalAmount = decimal.Parse(reader.GetString(reader.GetOrdinal("total_amount")),
                CultureInfo.InvariantCulture),
            Currency = reader.GetString(reader.GetOrdinal("currency")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")),
                CultureInfo.InvariantCulture),
            CorrelationId = reader.GetString(reader.GetOrdinal("correlation_id"))
        };
    }

    public void Dispose()
    {
        _connection.Dispose();
        _writeLock.Dispose();
    }
}
