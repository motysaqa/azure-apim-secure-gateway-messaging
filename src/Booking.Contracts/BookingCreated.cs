using System.Text.Json.Serialization;

namespace Booking.Contracts;

/// <summary>
/// The event published when a booking is accepted.
///
/// This is a contract between two independently deployed services, so it is
/// versioned explicitly and every field is either required from day one or
/// nullable. Adding a non-nullable field later is a breaking change; adding a
/// nullable one is not.
/// </summary>
public sealed record BookingCreated
{
    /// <summary>Schema version. The worker refuses versions it does not understand.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Stable identity of this message. Used as the Service Bus MessageId and as
    /// the worker's deduplication key, so a redelivery is a no-op rather than a
    /// second booking.
    /// </summary>
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("bookingId")]
    public required string BookingId { get; init; }

    [JsonPropertyName("propertyId")]
    public required string PropertyId { get; init; }

    [JsonPropertyName("guestEmail")]
    public required string GuestEmail { get; init; }

    [JsonPropertyName("checkIn")]
    public required DateOnly CheckIn { get; init; }

    [JsonPropertyName("checkOut")]
    public required DateOnly CheckOut { get; init; }

    [JsonPropertyName("guests")]
    public required int Guests { get; init; }

    [JsonPropertyName("totalAmount")]
    public required decimal TotalAmount { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Ties the message back to the HTTP request that produced it.</summary>
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;
}
