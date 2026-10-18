using System.Net;
using System.Net.Http.Json;
using MeetingRoomBooking.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class BookingConcurrencyTests
{
    private static readonly DateTimeOffset Start =
        new(2031, 4, 3, 9, 0, 0, TimeSpan.Zero);

    private static object Request(int roomId, string title, DateTimeOffset start) =>
        new
        {
            roomId,
            title,
            bookedBy = "Concurrency test",
            startUtc = start,
            endUtc = start.AddHours(1)
        };

    [Fact]
    public async Task TwoSimultaneousRequests_CannotDoubleBookSameRoom()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/bookings", Request(1, "First", Start)),
            client.PostAsJsonAsync("/api/bookings", Request(1, "Second", Start)));

        Assert.Equal(
            1,
            responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(
            1,
            responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        foreach (var response in responses)
        {
            response.Dispose();
        }

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        Assert.Equal(1, await context.Bookings.CountAsync());
    }

    [Fact]
    public async Task DirectSqlInsert_CannotBypassOverlapProtection()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/bookings", Request(1, "Existing", Start));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Bookings"
                    ("RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
                VALUES (1, 'Bypass', 'Concurrency test', {0}, {1}, {2})
                """,
                Start.AddMinutes(30).UtcDateTime.Ticks,
                Start.AddMinutes(90).UtcDateTime.Ticks,
                Start.UtcDateTime.Ticks));

        Assert.Contains("booking_overlap", exception.Message);
        Assert.Equal(1, await context.Bookings.CountAsync());
    }

    [Fact]
    public async Task DirectSqlUpdate_CannotMakeExistingBookingsOverlap()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            "/api/bookings", Request(1, "Existing", Start));
        using var second = await client.PostAsJsonAsync(
            "/api/bookings", Request(1, "Later", Start.AddHours(1)));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => context.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Bookings"
                SET "StartUtc" = {0}, "EndUtc" = {1}
                WHERE "Title" = 'Later'
                """,
                Start.AddMinutes(30).UtcDateTime.Ticks,
                Start.AddMinutes(90).UtcDateTime.Ticks));

        Assert.Contains("booking_overlap", exception.Message);
        Assert.Equal(2, await context.Bookings.CountAsync());
    }
}
