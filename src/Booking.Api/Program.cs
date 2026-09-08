using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Booking.Api.Messaging;
using Booking.Api.Storage;
using Booking.Contracts;

// Booking API.
//
// It is deliberately naive about security: there is no authentication code here
// at all. Every request is expected to have passed through Azure API Management,
// which validates the Entra ID JWT, enforces the subscription key, rate-limits by
// caller and filters by IP. Duplicating that in the app would mean two places to
// get wrong; the App Service is instead locked to the APIM outbound IPs, which is
// what makes the assumption safe. See docs/apim-request-flow.md.

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSingleton<BookingStore>();

// No Service Bus namespace configured means local development, so the app still
// runs end to end and the outbox is still exercised - it just has nowhere to send.
if (!string.IsNullOrWhiteSpace(builder.Configuration["ServiceBus:FullyQualifiedNamespace"]))
    builder.Services.AddSingleton<IMessagePublisher, ServiceBusMessagePublisher>();
else
    builder.Services.AddSingleton<IMessagePublisher, InMemoryMessagePublisher>();

builder.Services.AddHostedService<OutboxPublisher>();

var app = builder.Build();

// Correlation id on every request, generated when the caller does not supply one.
// APIM sets it too (set-header policy), so one id spans gateway, API and worker.
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
        correlationId = Guid.NewGuid().ToString();

    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    await next();
});

// Defence in depth for App Service ingress.
//
// APIM injects this header from a Key Vault-backed named value, so a request that
// reaches the App Service URL directly - bypassing every gateway policy - is
// rejected here. Unset in local development, so the app still runs from a clone.
// It is a second lock, not the first one: the IP restriction in infra/ is what
// actually keeps direct traffic out.
var requiredBackendKey = builder.Configuration["Gateway:BackendKey"];
if (!string.IsNullOrWhiteSpace(requiredBackendKey))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next();
            return;
        }

        var presented = context.Request.Headers["X-Backend-Key"].FirstOrDefault();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(requiredBackendKey)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemResponse
            {
                Type = "https://httpstatuses.io/401",
                Title = "Direct access is not permitted",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "This API is only reachable through the API Management gateway.",
                CorrelationId = (string)context.Items["CorrelationId"]!
            });
            return;
        }

        await next();
    });
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
   .WithName("Health");

app.MapGet("/health/ready", async (BookingStore store, IMessagePublisher publisher, CancellationToken ct) =>
{
    // Readiness reports the outbox depth because that is the number that tells you
    // whether messaging is actually working. A growing backlog is the first
    // symptom of a broken managed identity or a deleted queue.
    var (total, pending) = await store.OutboxDepth(ct);
    return Results.Ok(new
    {
        status = "ready",
        outbox = new { total, pending },
        destination = publisher.Destination
    });
}).WithName("Readiness");

app.MapPost("/bookings", async (
    CreateBookingRequest request,
    BookingStore store,
    HttpContext context,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var correlationId = (string)context.Items["CorrelationId"]!;
    var logger = loggerFactory.CreateLogger("Booking.Api.Create");

    var errors = BookingValidator.Validate(request, DateOnly.FromDateTime(DateTime.UtcNow));
    if (errors.Count > 0)
    {
        logger.LogInformation("Rejected booking request with {ErrorCount} validation error(s) (correlation {CorrelationId})",
            errors.Count, correlationId);

        return Results.Json(new ProblemResponse
        {
            Type = "https://httpstatuses.io/400",
            Title = "Invalid booking request",
            Status = StatusCodes.Status400BadRequest,
            Detail = "One or more fields failed validation.",
            CorrelationId = correlationId,
            Errors = errors
        }, statusCode: StatusCodes.Status400BadRequest, contentType: "application/problem+json");
    }

    // A client retrying after a timeout must not create a second booking. The key
    // is the client's, not ours: only the caller knows that two requests are the
    // same intent.
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();

    var (booking, created) = await store.TryCreate(request, idempotencyKey, correlationId, cancellationToken);

    if (!created)
    {
        // 200, not 201: nothing was created this time. The body is the original
        // booking, so a retry is indistinguishable from success for the client.
        context.Response.Headers["Idempotency-Replayed"] = "true";
        return Results.Ok(booking);
    }

    return Results.Created($"/bookings/{booking.BookingId}", booking);
}).WithName("CreateBooking");

app.MapGet("/bookings/{bookingId}", async (
    string bookingId,
    BookingStore store,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var correlationId = (string)context.Items["CorrelationId"]!;
    var booking = await store.Find(bookingId, cancellationToken);

    return booking is null
        ? Results.Json(new ProblemResponse
        {
            Type = "https://httpstatuses.io/404",
            Title = "Booking not found",
            Status = StatusCodes.Status404NotFound,
            Detail = $"No booking with id '{bookingId}'.",
            CorrelationId = correlationId
        }, statusCode: StatusCodes.Status404NotFound, contentType: "application/problem+json")
        : Results.Ok(booking);
}).WithName("GetBooking");

// Used by scripts/reconcile_bookings.py to compare the API's view against the
// worker's store. Not part of the public product; APIM does not expose it.
app.MapGet("/internal/bookings", async (
    BookingStore store, int? limit, CancellationToken cancellationToken) =>
{
    var bookings = await store.ListBookings(Math.Clamp(limit ?? 100, 1, 1000), cancellationToken);
    var (total, pending) = await store.OutboxDepth(cancellationToken);
    return Results.Ok(new { items = bookings, count = bookings.Count, outbox = new { total, pending } });
}).WithName("ListBookingsInternal");

app.Run();

/// <summary>Exposed so the integration tests can host the app in-process.</summary>
public partial class Program;
