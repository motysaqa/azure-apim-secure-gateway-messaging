using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Booking.Api.Messaging;
using Booking.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Booking.Api.Tests;

public sealed class BookingEndpointsTests : IClassFixture<BookingApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BookingApiFactory _factory;

    public BookingEndpointsTests(BookingApiFactory factory) => _factory = factory;

    private static CreateBookingRequest ValidRequest(string propertyId = "PROP-1001") => new()
    {
        PropertyId = propertyId,
        GuestEmail = "guest@example.com",
        CheckIn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
        CheckOut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(34),
        Guests = 2,
        TotalAmount = 592.40m,
        Currency = "EUR"
    };

    [Fact]
    public async Task Health_reports_healthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_booking_returns_201_with_a_pending_booking()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/bookings", ValidRequest(), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var booking = await response.Content.ReadFromJsonAsync<BookingResponse>(Json);
        Assert.NotNull(booking);
        Assert.StartsWith("BK-", booking!.BookingId);
        Assert.Equal(4, booking.Nights);
        Assert.Equal("EUR", booking.Currency);
        // PENDING, not CONFIRMED: the booking is only confirmed once its outbox
        // message has actually been published.
        Assert.Equal("PENDING", booking.Status);
        Assert.Equal($"/bookings/{booking.BookingId}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Money_survives_the_round_trip_through_storage()
    {
        var client = _factory.CreateClient();
        var request = ValidRequest() with { TotalAmount = 148.50m };

        var created = await client.PostAsJsonAsync("/bookings", request, Json);
        var booking = await created.Content.ReadFromJsonAsync<BookingResponse>(Json);

        var fetched = await client.GetFromJsonAsync<BookingResponse>($"/bookings/{booking!.BookingId}", Json);

        // Stored as text rather than SQLite's REAL, which is a double and would
        // turn 148.50 into 148.49999999999997.
        Assert.Equal(148.50m, fetched!.TotalAmount);
    }

    [Fact]
    public async Task A_supplied_correlation_id_is_echoed_and_stored()
    {
        var client = _factory.CreateClient();
        var correlationId = Guid.NewGuid().ToString();

        using var message = new HttpRequestMessage(HttpMethod.Post, "/bookings")
        {
            Content = JsonContent.Create(ValidRequest(), options: Json)
        };
        message.Headers.Add("X-Correlation-ID", correlationId);

        var response = await client.SendAsync(message);
        var booking = await response.Content.ReadFromJsonAsync<BookingResponse>(Json);

        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal(correlationId, booking!.CorrelationId);
    }

    [Fact]
    public async Task A_correlation_id_is_generated_when_the_caller_omits_one()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/bookings", ValidRequest(), Json);
        var booking = await response.Content.ReadFromJsonAsync<BookingResponse>(Json);

        Assert.False(string.IsNullOrWhiteSpace(booking!.CorrelationId));
        Assert.True(Guid.TryParse(booking.CorrelationId, out _));
    }

    [Fact]
    public async Task Replaying_an_idempotency_key_returns_the_original_booking()
    {
        var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        async Task<HttpResponseMessage> Post()
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/bookings")
            {
                Content = JsonContent.Create(ValidRequest(), options: Json)
            };
            message.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(message);
        }

        var first = await Post();
        var second = await Post();

        var firstBooking = await first.Content.ReadFromJsonAsync<BookingResponse>(Json);
        var secondBooking = await second.Content.ReadFromJsonAsync<BookingResponse>(Json);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        // 200, not 201: nothing was created the second time.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(firstBooking!.BookingId, secondBooking!.BookingId);
        Assert.Equal(firstBooking.CreatedAt, secondBooking.CreatedAt);
    }

    [Fact]
    public async Task Different_idempotency_keys_create_different_bookings()
    {
        var client = _factory.CreateClient();

        async Task<BookingResponse> PostWithKey(string key)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/bookings")
            {
                Content = JsonContent.Create(ValidRequest(), options: Json)
            };
            message.Headers.Add("Idempotency-Key", key);
            var response = await client.SendAsync(message);
            return (await response.Content.ReadFromJsonAsync<BookingResponse>(Json))!;
        }

        var first = await PostWithKey(Guid.NewGuid().ToString());
        var second = await PostWithKey(Guid.NewGuid().ToString());

        Assert.NotEqual(first.BookingId, second.BookingId);
    }

    [Theory]
    [InlineData("propertyId", null, "guest@example.com", 2, "EUR")]
    [InlineData("guestEmail", "PROP-1", "not-an-email", 2, "EUR")]
    [InlineData("guests", "PROP-1", "guest@example.com", 0, "EUR")]
    [InlineData("currency", "PROP-1", "guest@example.com", 2, "XYZ")]
    public async Task Invalid_requests_are_rejected_with_a_field_level_problem_document(
        string expectedField, string? propertyId, string email, int guests, string currency)
    {
        var client = _factory.CreateClient();
        var request = ValidRequest() with
        {
            PropertyId = propertyId,
            GuestEmail = email,
            Guests = guests,
            Currency = currency
        };

        var response = await client.PostAsJsonAsync("/bookings", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(Json);
        Assert.NotNull(problem!.Errors);
        // The client is told which field is wrong, not just that something is.
        Assert.Contains(expectedField, problem.Errors!.Keys);
        Assert.False(string.IsNullOrWhiteSpace(problem.CorrelationId));
    }

    [Fact]
    public async Task A_stay_that_ends_before_it_starts_is_rejected()
    {
        var client = _factory.CreateClient();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var request = ValidRequest() with { CheckIn = today.AddDays(10), CheckOut = today.AddDays(10) };

        var response = await client.PostAsJsonAsync("/bookings", request, Json);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("checkOut", problem!.Errors!.Keys);
    }

    [Fact]
    public async Task An_unknown_booking_returns_a_404_problem_document()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/bookings/BK-DOES-NOT-EXIST");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(Json);
        Assert.Equal(404, problem!.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.CorrelationId));
    }

    [Fact]
    public async Task A_created_booking_is_eventually_published_and_confirmed()
    {
        // Its own factory: this test depends on the outbox draining, which would
        // otherwise race with the tests that expect a frozen outbox.
        using var factory = new BookingApiFactory { OutboxPollIntervalSeconds = 1 };
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/bookings", ValidRequest("PROP-OUTBOX"), Json);
        var booking = await response.Content.ReadFromJsonAsync<BookingResponse>(Json);

        var publisher = (InMemoryMessagePublisher)factory.Services.GetRequiredService<IMessagePublisher>();

        var published = await WaitFor(
            () => publisher.Published.Any(p => p.Payload.Contains(booking!.BookingId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15));

        Assert.True(published, "the outbox never published the booking within 15 seconds");

        var message = publisher.Published.First(p =>
            p.Payload.Contains(booking!.BookingId, StringComparison.Ordinal));
        var deserialised = JsonSerializer.Deserialize<BookingCreated>(message.Payload, Json);

        Assert.Equal(booking!.BookingId, deserialised!.BookingId);
        Assert.Equal(booking.CorrelationId, deserialised.CorrelationId);
        Assert.Equal(booking.TotalAmount, deserialised.TotalAmount);
        Assert.Equal(1, deserialised.Version);

        // The status flips to CONFIRMED only after the message is actually out.
        var confirmed = await WaitFor(async () =>
        {
            var refreshed = await client.GetFromJsonAsync<BookingResponse>($"/bookings/{booking.BookingId}", Json);
            return refreshed!.Status == "CONFIRMED";
        }, TimeSpan.FromSeconds(15));

        Assert.True(confirmed, "the booking was published but never marked CONFIRMED");
    }

    [Fact]
    public async Task The_readiness_probe_reports_the_outbox_depth()
    {
        // A frozen outbox: the message is written but never drained, which is what
        // proves the booking and its message are committed together rather than
        // the message being sent from inside the request.
        using var factory = new BookingApiFactory { OutboxPollIntervalSeconds = 3600 };
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/bookings", ValidRequest("PROP-FROZEN"), Json);

        using var readiness = await client.GetAsync("/health/ready");
        var body = await readiness.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.True(body.GetProperty("outbox").GetProperty("pending").GetInt32() >= 1);
        Assert.Contains("in-memory", body.GetProperty("destination").GetString()!);
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout) =>
        await WaitFor(() => Task.FromResult(condition()), timeout);

    private static async Task<bool> WaitFor(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(200);
        }
        return false;
    }
}
