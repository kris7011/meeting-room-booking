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
    private readonly string databasePath =
        Path.Combine(
            Path.GetTempPath(),
            $"meeting-room-booking-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(
        IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

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
                            $"Data Source={databasePath};Pooling=False"));
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