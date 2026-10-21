using MeetingRoomBooking.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class MeetingRoomBookingApiFactory
    : WebApplicationFactory<Program>
{
    /// <summary>
    /// The server's "now" unless a test sets <see cref="Clock"/>. It lies
    /// before every booking the tests create (2030 and later), so those stay
    /// in the future however much real time passes.
    /// </summary>
    public static readonly DateTimeOffset DefaultNow =
        new(2029, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The time source the API uses; tests can move it.</summary>
    public TestTimeProvider Clock { get; } =
        new(DefaultNow);

    private readonly string databasePath =
        Path.Combine(
            Path.GetTempPath(),
            $"meeting-room-booking-tests-{Guid.NewGuid():N}.db");

    /// <summary>The test's own SQLite file under the system temp folder.</summary>
    public string DatabasePath => databasePath;

    public string ConnectionString =>
        $"Data Source={databasePath};Pooling=False";

    private string BackupDirectory =>
        $"{databasePath}-backups";

    protected override void ConfigureWebHost(
        IWebHostBuilder builder)
    {
        TestDatabaseGuard.EnsureIsolated(databasePath);

        builder.UseEnvironment("Testing");

        // Set in configuration as well, so Program never even reads the
        // development connection string from appsettings.json.
        builder.UseSetting(
            "ConnectionStrings:BookingDatabase",
            ConnectionString);

        builder.UseSetting(
            "Database:BackupDirectory",
            BackupDirectory);

        builder.ConfigureServices(
            services =>
            {
                services.RemoveAll<
                    DbContextOptions<BookingDbContext>>();

                services.RemoveAll<
                    BookingDbContext>();

                services.AddDbContext<BookingDbContext>(
                    options =>
                        options.UseSqlite(
                            ConnectionString));

                services.RemoveAll<TimeProvider>();

                services.AddSingleton<TimeProvider>(
                    Clock);
            });
    }

    protected override void Dispose(
        bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        DeleteDatabaseFile(
            databasePath);

        DeleteDatabaseFile(
            $"{databasePath}-shm");

        DeleteDatabaseFile(
            $"{databasePath}-wal");

        if (Directory.Exists(BackupDirectory))
        {
            Directory.Delete(
                BackupDirectory,
                recursive: true);
        }
    }

    private static void DeleteDatabaseFile(
        string path)
    {
        const int maximumAttempts = 5;

        for (var attempt = 1;
            attempt <= maximumAttempts;
            attempt++)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);

                return;
            }
            catch (IOException)
                when (attempt < maximumAttempts)
            {
                Thread.Sleep(
                    millisecondsTimeout:
                        attempt * 50);
            }
        }
    }
}