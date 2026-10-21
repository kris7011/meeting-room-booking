namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>
/// Stops a test before it opens a database outside the system temp folder,
/// such as the development database next to the API project.
/// </summary>
public static class TestDatabaseGuard
{
    public const string DevelopmentDatabaseFileName =
        "meeting-room-booking.db";

    public static void EnsureIsolated(
        string databasePath)
    {
        var fullPath =
            Path.GetFullPath(databasePath);

        var tempFolder =
            Path.GetFullPath(Path.GetTempPath());

        if (!fullPath.StartsWith(
                tempFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Tests may only use databases under {tempFolder}, not {fullPath}.");
        }

        var apiProjectFolder =
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..", "..", "..", "..", "..",
                    "src",
                    "MeetingRoomBooking.Api"));

        if (fullPath.StartsWith(
                apiProjectFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Tests must never use the development database folder {apiProjectFolder}.");
        }
    }
}
