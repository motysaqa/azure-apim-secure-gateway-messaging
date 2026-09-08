using System.Text.Json.Serialization;

namespace Booking.Contracts;

public sealed record CreateBookingRequest
{
    [JsonPropertyName("propertyId")]
    public string? PropertyId { get; init; }

    [JsonPropertyName("guestEmail")]
    public string? GuestEmail { get; init; }

    [JsonPropertyName("checkIn")]
    public DateOnly? CheckIn { get; init; }

    [JsonPropertyName("checkOut")]
    public DateOnly? CheckOut { get; init; }

    [JsonPropertyName("guests")]
    public int? Guests { get; init; }

    [JsonPropertyName("totalAmount")]
    public decimal? TotalAmount { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

public sealed record BookingResponse
{
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

    [JsonPropertyName("nights")]
    public required int Nights { get; init; }

    [JsonPropertyName("guests")]
    public required int Guests { get; init; }

    [JsonPropertyName("totalAmount")]
    public required decimal TotalAmount { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
}

/// <summary>
/// RFC 7807-shaped error body. APIM policies return the same shape for the errors
/// they generate (401, 429), so a client only ever parses one error format.
/// </summary>
public sealed record ProblemResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "about:blank";

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("status")]
    public required int Status { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
