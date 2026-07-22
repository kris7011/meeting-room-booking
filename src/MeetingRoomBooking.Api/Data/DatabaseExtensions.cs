using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Data;

public static class DatabaseExtensions
{
    public static async Task InitialiseDatabaseAsync(
        this WebApplication application)
    {
        await using var scope =
            application.Services.CreateAsyncScope();

        var dbContext =
            scope.ServiceProvider
                .GetRequiredService<BookingDbContext>();

        await dbContext.Database.MigrateAsync();
    }
}