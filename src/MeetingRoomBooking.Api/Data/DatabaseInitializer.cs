using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Data;

/// <param name="BackupPath">The verified backup taken before migrating, if any.</param>
/// <param name="AppliedMigrations">The migrations applied by this start-up.</param>
public sealed record DatabaseInitializationResult(
    string? BackupPath,
    IReadOnlyList<string> AppliedMigrations);

/// <summary>
/// Brings the database up to date at start-up. An existing database is never
/// migrated without a verified backup, and the API does not start if the
/// migrated database fails its checks.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// The SQLite triggers that keep two bookings of the same room from
    /// overlapping (see PreventBookingOverlap).
    /// </summary>
    public static readonly IReadOnlyList<string> OverlapTriggers =
    [
        "TR_Bookings_Overlap_Insert",
        "TR_Bookings_Overlap_Update"
    ];

    public static async Task<DatabaseInitializationResult> InitialiseAsync(
        BookingDbContext dbContext,
        string? backupDirectory,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var databaseFile =
            DatabaseBackup.ResolveDatabaseFile(
                dbContext.Database.GetConnectionString());

        // Checked before anything opens the database: opening it creates
        // an empty file, which has nothing to back up.
        var databaseExisted =
            databaseFile is not null
            && File.Exists(databaseFile)
            && new FileInfo(databaseFile).Length > 0;

        var pendingMigrations =
            (await dbContext.Database
                .GetPendingMigrationsAsync(cancellationToken))
            .ToArray();

        string? backupPath = null;

        if (pendingMigrations.Length > 0)
        {
            var appliedMigrations =
                await dbContext.Database
                    .GetAppliedMigrationsAsync(cancellationToken);

            // A non-empty file without EF Core migration history was not
            // created by these migrations, so its schema is unknown. Migrating
            // it would run every migration from the first one against tables
            // that may already exist, and there would be no backup. Stop and
            // leave the file exactly as it is.
            if (databaseExisted && !appliedMigrations.Any())
            {
                throw new InvalidOperationException(
                    $"The database {databaseFile} is not empty but has no Entity Framework Core migration history " +
                    $"(\"__EFMigrationsHistory\" is missing or empty), so its schema is unknown. " +
                    "The API did not migrate, back up or change it. Inspect the file manually, or point " +
                    "ConnectionStrings:BookingDatabase at a new file to start with an empty database.");
            }

            if (databaseExisted)
            {
                backupPath =
                    await DatabaseBackup.CreateVerifiedAsync(
                        databaseFile!,
                        backupDirectory,
                        pendingMigrations[0],
                        timeProvider,
                        cancellationToken);

                logger.LogInformation(
                    "Backed up the database to {BackupPath} before applying {PendingMigrationCount} migration(s).",
                    backupPath,
                    pendingMigrations.Length);
            }

            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        await VerifyAsync(
            dbContext,
            backupPath,
            cancellationToken);

        return new DatabaseInitializationResult(
            backupPath,
            pendingMigrations);
    }

    /// <summary>
    /// Throws unless foreign keys and integrity are intact and both overlap
    /// triggers exist.
    /// </summary>
    public static async Task VerifyAsync(
        BookingDbContext dbContext,
        string? backupPath,
        CancellationToken cancellationToken)
    {
        var restoreHint =
            backupPath is null
                ? "No backup was taken by this start-up."
                : $"The database before migrating is in {backupPath}.";

        var connection =
            dbContext.Database.GetDbConnection();

        var wasClosed =
            connection.State == ConnectionState.Closed;

        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var foreignKeyCheck = connection.CreateCommand())
            {
                foreignKeyCheck.CommandText = "PRAGMA foreign_key_check";

                await using var reader =
                    await foreignKeyCheck.ExecuteReaderAsync(cancellationToken);

                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"The database has foreign key violations, for example in table {reader.GetString(0)}. {restoreHint}");
                }
            }

            await using (var integrityCheck = connection.CreateCommand())
            {
                integrityCheck.CommandText = "PRAGMA integrity_check";

                var result =
                    await integrityCheck.ExecuteScalarAsync(cancellationToken);

                if (!string.Equals(
                        result as string,
                        "ok",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The database failed its integrity check ({result}). {restoreHint}");
                }
            }

            await using (var triggerCheck = connection.CreateCommand())
            {
                triggerCheck.CommandText =
                    """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE "type" = 'trigger' AND "tbl_name" = 'Bookings'
                      AND "name" IN ('TR_Bookings_Overlap_Insert', 'TR_Bookings_Overlap_Update')
                    """;

                var count =
                    (long)(await triggerCheck.ExecuteScalarAsync(cancellationToken))!;

                if (count != OverlapTriggers.Count)
                {
                    throw new InvalidOperationException(
                        $"The database is missing a booking overlap trigger, so overlapping bookings would not be rejected. {restoreHint}");
                }
            }
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }
}
