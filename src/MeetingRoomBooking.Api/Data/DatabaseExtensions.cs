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

        var logger =
            scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(DatabaseInitializer));

        // Defaults to a "backups" folder next to the database file.
        await DatabaseInitializer.InitialiseAsync(
            dbContext,
            application.Configuration["Database:BackupDirectory"],
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            logger);
    }
}
