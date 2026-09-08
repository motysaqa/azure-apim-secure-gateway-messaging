namespace Booking.Contracts;

/// <summary>
/// Request validation, kept in the shared assembly so the API and the worker
/// agree on what a valid booking is. The worker re-validates rather than trusting
/// the queue: a message can outlive the code that produced it.
/// </summary>
public static class BookingValidator
{
    private static readonly string[] SupportedCurrencies = ["EUR", "USD", "GBP", "AED", "SAR"];
    public const int MaxNights = 30;
    public const int MaxGuests = 20;

    public static IReadOnlyDictionary<string, string[]> Validate(CreateBookingRequest request, DateOnly today)
    {
        var errors = new Dictionary<string, List<string>>();

        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                list = [];
                errors[field] = list;
            }
            list.Add(message);
        }

        if (string.IsNullOrWhiteSpace(request.PropertyId))
            Add("propertyId", "propertyId is required.");

        if (string.IsNullOrWhiteSpace(request.GuestEmail))
            Add("guestEmail", "guestEmail is required.");
        else if (!LooksLikeEmail(request.GuestEmail))
            Add("guestEmail", "guestEmail is not a valid email address.");

        if (request.CheckIn is null)
            Add("checkIn", "checkIn is required (yyyy-MM-dd).");
        if (request.CheckOut is null)
            Add("checkOut", "checkOut is required (yyyy-MM-dd).");

        if (request.CheckIn is { } checkIn && request.CheckOut is { } checkOut)
        {
            if (checkOut <= checkIn)
                Add("checkOut", "checkOut must be at least one day after checkIn.");
            else if (checkOut.DayNumber - checkIn.DayNumber > MaxNights)
                Add("checkOut", $"A stay may not exceed {MaxNights} nights.");

            if (checkIn < today)
                Add("checkIn", "checkIn may not be in the past.");
        }

        switch (request.Guests)
        {
            case null:
                Add("guests", "guests is required.");
                break;
            case < 1:
                Add("guests", "guests must be at least 1.");
                break;
            case > MaxGuests:
                Add("guests", $"guests may not exceed {MaxGuests}.");
                break;
        }

        switch (request.TotalAmount)
        {
            case null:
                Add("totalAmount", "totalAmount is required.");
                break;
            case < 0:
                Add("totalAmount", "totalAmount may not be negative.");
                break;
        }

        if (string.IsNullOrWhiteSpace(request.Currency))
            Add("currency", "currency is required.");
        else if (!SupportedCurrencies.Contains(request.Currency.ToUpperInvariant()))
            Add("currency", $"currency must be one of: {string.Join(", ", SupportedCurrencies)}.");

        return errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    /// <summary>
    /// Deliberately not a full RFC 5322 implementation. A regex that tries to be
    /// one is unreadable, still wrong, and a common ReDoS source; the real check
    /// is sending a confirmation email.
    /// </summary>
    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1) return false;
        if (value.IndexOf('@', at + 1) >= 0) return false;

        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.')
               && !value.Contains(' ');
    }
}
