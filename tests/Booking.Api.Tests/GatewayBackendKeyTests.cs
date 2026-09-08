using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Booking.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Booking.Api.Tests;

/// <summary>
/// The App Service is meant to be reachable only through API Management, which
/// injects a shared key from a Key Vault-backed named value. These tests cover
/// the app's half of that: what happens to a request that arrives without it.
/// </summary>
public sealed class GatewayBackendKeyTests
{
    private const string BackendKey = "test-backend-key-value";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class GuardedFactory : WebApplicationFactory<Program>
    {
        private readonly string _databasePath =
            Path.Combine(Path.GetTempPath(), $"booking-gateway-tests-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Bookings"] = $"Data Source={_databasePath}",
                    ["ServiceBus:FullyQualifiedNamespace"] = "",
                    ["Gateway:BackendKey"] = BackendKey,
                    ["Outbox:PollIntervalSeconds"] = "3600"
                }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            try
            {
                if (File.Exists(_databasePath)) File.Delete(_databasePath);
            }
            catch (IOException) { }
        }
    }

    private static CreateBookingRequest ValidRequest() => new()
    {
        PropertyId = "PROP-1001",
        GuestEmail = "guest@example.com",
        CheckIn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10),
        CheckOut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(12),
        Guests = 2,
        TotalAmount = 240.00m,
        Currency = "EUR"
    };

    [Fact]
    public async Task A_request_without_the_backend_key_is_rejected()
    {
        using var factory = new GuardedFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/bookings", ValidRequest(), Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(Json);
        Assert.Equal(401, problem!.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.CorrelationId));
    }

    [Fact]
    public async Task A_request_with_the_wrong_backend_key_is_rejected()
    {
        using var factory = new GuardedFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Backend-Key", "not-the-key");

        var response = await client.PostAsJsonAsync("/bookings", ValidRequest(), Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_carrying_the_backend_key_is_served()
    {
        using var factory = new GuardedFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Backend-Key", BackendKey);

        var response = await client.PostAsJsonAsync("/bookings", ValidRequest(), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Health_stays_reachable_without_the_backend_key()
    {
        // Otherwise the App Service health check fails and Azure restarts a
        // perfectly healthy instance in a loop.
        using var factory = new GuardedFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
