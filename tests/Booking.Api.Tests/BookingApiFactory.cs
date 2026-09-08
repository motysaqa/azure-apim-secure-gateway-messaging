using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Booking.Api.Tests;

/// <summary>
/// Hosts the real application in-process against a throwaway SQLite file.
///
/// The API is not stubbed: the same endpoints, the same store and the same outbox
/// publisher run here as in production. Only two things are steered - where the
/// database lives, and how eagerly the outbox drains - because a test that has to
/// wait two seconds for a background loop is a test nobody runs.
/// </summary>
public sealed class BookingApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"booking-api-tests-{Guid.NewGuid():N}.db");

    /// <summary>Seconds between outbox drains. Set high to freeze the outbox and inspect it.</summary>
    public int OutboxPollIntervalSeconds { get; init; } = 1;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Bookings"] = $"Data Source={_databasePath}",
                // Empty on purpose: the app then uses InMemoryMessagePublisher, so
                // the tests never touch Azure and need no credentials.
                ["ServiceBus:FullyQualifiedNamespace"] = "",
                ["Outbox:PollIntervalSeconds"] = OutboxPollIntervalSeconds.ToString(),
                ["Outbox:BatchSize"] = "20"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        try
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(_databasePath + suffix)) File.Delete(_databasePath + suffix);
        }
        catch (IOException)
        {
            // A locked temp file is not worth failing a test run over.
        }
    }
}
