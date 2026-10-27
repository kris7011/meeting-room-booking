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

    /// <summary>
    /// The test's own SQLite file under the system temp folder: a new one by
    /// default, or an existing one, such as a restored backup, to start the
    /// API on. It is deleted when the factory is disposed, unless
    /// <see cref="KeepDatabaseOnDispose"/> is set.
    /// </summary>
    public string DatabasePath { get; init; } =
        Path.Combine(
            Path.GetTempPath(),
            $"meeting-room-booking-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Leaves the database file behind on dispose, so a test can stop this
    /// API and start another one on the same file (an API restart). The
    /// next factory on the file deletes it.
    /// </summary>
    public bool KeepDatabaseOnDispose { get; init; }

    public string ConnectionString =>
        $"Data Source={DatabasePath};Pooling=False";

    private string BackupDirectory =>
        $"{DatabasePath}-backups";

    /// <summary>
    /// <c>LegacyApi:AllowAnonymous</c>. The suites written before sign-in
    /// existed exercise /api/rooms and /api/bookings in the anonymous demo
    /// mode, so it is on by default here; the access-control tests turn it
    /// off, which is the API's own default.
    /// </summary>
    public bool AllowAnonymousLegacyApi { get; init; } =
        true;

    protected override void ConfigureWebHost(
        IWebHostBuilder builder)
    {
        TestDatabaseGuard.EnsureIsolated(DatabasePath);

        builder.UseEnvironment("Testing");

        builder.UseSetting(
            "LegacyApi:AllowAnonymous",
            AllowAnonymousLegacyApi ? "true" : "false");

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

        if (!disposing ||
            KeepDatabaseOnDispose)
        {
            return;
        }

        DeleteDatabaseFile(
            DatabasePath);

        DeleteDatabaseFile(
            $"{DatabasePath}-shm");

        DeleteDatabaseFile(
            $"{DatabasePath}-wal");

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