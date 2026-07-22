using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class MeetingRoomBookingApiFactory
    : WebApplicationFactory<Program>
{
    private readonly string databasePath =
        Path.Combine(
            Path.GetTempPath(),
            $"meeting-room-booking-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(
        IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(
            (_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:BookingDatabase"] =
                            $"Data Source={databasePath}"
                    });
            });
    }

    protected override void Dispose(
        bool disposing)
    {
        base.Dispose(disposing);

        DeleteDatabaseFile(databasePath);
        DeleteDatabaseFile($"{databasePath}-shm");
        DeleteDatabaseFile($"{databasePath}-wal");
    }

    private static void DeleteDatabaseFile(
        string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}