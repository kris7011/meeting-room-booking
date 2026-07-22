using MeetingRoomBooking.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class DatabaseStartupTests(
    MeetingRoomBookingApiFactory factory)
    : IClassFixture<MeetingRoomBookingApiFactory>
{
    [Fact]
    public async Task Startup_AppliesMigrationAndSeedsMeetingRooms()
    {
        using var scope =
            factory.Services.CreateScope();

        var dbContext =
            scope.ServiceProvider
                .GetRequiredService<BookingDbContext>();

        var rooms =
            await dbContext.MeetingRooms
                .AsNoTracking()
                .OrderBy(room => room.Id)
                .ToArrayAsync();

        Assert.Collection(
            rooms,
            room =>
            {
                Assert.Equal(
                    "Focus Room",
                    room.Name);

                Assert.Equal(
                    4,
                    room.Capacity);
            },
            room =>
            {
                Assert.Equal(
                    "Collaboration Room",
                    room.Name);

                Assert.Equal(
                    8,
                    room.Capacity);
            },
            room =>
            {
                Assert.Equal(
                    "Board Room",
                    room.Name);

                Assert.Equal(
                    12,
                    room.Capacity);
            });

        Assert.Empty(
            await dbContext.Bookings
                .AsNoTracking()
                .ToArrayAsync());

        var appliedMigrations =
            await dbContext.Database
                .GetAppliedMigrationsAsync();

        Assert.Contains(
            appliedMigrations,
            migration =>
                migration.EndsWith(
                    "_InitialCreate",
                    StringComparison.Ordinal));
    }
}