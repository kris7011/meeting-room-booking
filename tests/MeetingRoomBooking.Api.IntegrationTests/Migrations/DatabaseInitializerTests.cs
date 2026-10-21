using MeetingRoomBooking.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// The start-up path: verified backup, migration, checks, and a restore of a
/// real backup file. Everything runs on databases in temp folders.
/// </summary>
public sealed class DatabaseInitializerTests
{
    private const string LastMigrationSql =
        """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1""";

    private readonly TestTimeProvider clock =
        new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Startup_BacksUpBeforePendingMigrations()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        var roomsBefore = await database.QueryAsync(LegacySchemaFixture.SelectRoomsSql);
        var bookingsBefore = await database.QueryAsync(LegacySchemaFixture.SelectBookingsSql);

        var result = await InitialiseAsync(database);

        Assert.NotNull(result.BackupPath);
        Assert.StartsWith(database.BackupDirectoryPath, result.BackupPath);
        Assert.Equal(
            "meeting-room-booking.pre-20261021065822_CaptureSequenceState-20261021T120000000Z.db",
            Path.GetFileName(result.BackupPath));
        Assert.Equal(4, result.AppliedMigrations.Count);

        await using var backup = OpenReadOnly(result.BackupPath);
        Assert.Equal(
            "ok",
            (await MigrationTestDatabase.QueryAsync(backup, "PRAGMA integrity_check"))[0][0]);
        Assert.Equal(
            MigrationTestDatabase.PrePhase1Migration,
            (await MigrationTestDatabase.QueryAsync(backup, LastMigrationSql))[0][0]);
        Assert.Equal(
            roomsBefore,
            await MigrationTestDatabase.QueryAsync(backup, LegacySchemaFixture.SelectRoomsSql));
        Assert.Equal(
            bookingsBefore,
            await MigrationTestDatabase.QueryAsync(backup, LegacySchemaFixture.SelectBookingsSql));
        Assert.Empty(
            await MigrationTestDatabase.QueryAsync(
                backup,
                """SELECT 1 FROM pragma_table_info('Bookings') WHERE "name" = 'OrganizationId'"""));
    }

    [Fact]
    public async Task Startup_SkipsBackupWithoutPendingMigrations()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await InitialiseAsync(database);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var second = await InitialiseAsync(database);

        Assert.Null(second.BackupPath);
        Assert.Empty(second.AppliedMigrations);
        Assert.Single(Directory.GetFiles(database.BackupDirectoryPath));
    }

    [Fact]
    public async Task Startup_SkipsBackupForNewDatabase()
    {
        using var database = new MigrationTestDatabase();

        var result = await InitialiseAsync(database);

        Assert.Null(result.BackupPath);
        Assert.False(Directory.Exists(database.BackupDirectoryPath));
        Assert.Equal(
            "20261021070636_RestoreBookingInvariants",
            await database.ScalarAsync(LastMigrationSql));
    }

    [Fact]
    public async Task Startup_StopsWhenBackupFails()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        var checksumBefore = database.ComputeChecksum();

        // A file where the backup folder should be makes the backup fail.
        await File.WriteAllTextAsync(database.BackupDirectoryPath, "not a folder");

        await Assert.ThrowsAnyAsync<IOException>(
            () => InitialiseAsync(database));

        Assert.Equal(checksumBefore, database.ComputeChecksum());
        Assert.Equal(
            MigrationTestDatabase.PrePhase1Migration,
            await database.ScalarAsync(LastMigrationSql));
    }

    [Fact]
    public async Task Startup_RefusesToStartWhenAnOverlapTriggerIsMissing()
    {
        using var database = new MigrationTestDatabase();
        await InitialiseAsync(database);
        await database.ExecuteAsync("""DROP TRIGGER "TR_Bookings_Overlap_Update" """);

        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InitialiseAsync(database));

        Assert.Contains("overlap trigger", exception.Message);
    }

    [Fact]
    public async Task RestoreDrill_ReturnsPreviousSchemaAndRows()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        var roomsBefore = await database.QueryAsync(LegacySchemaFixture.SelectRoomsSql);
        var bookingsBefore = await database.QueryAsync(LegacySchemaFixture.SelectBookingsSql);
        const string schemaSql =
            """SELECT "type", "name", "sql" FROM sqlite_master ORDER BY "type", "name" """;
        var schemaBefore = await database.QueryAsync(schemaSql);

        var migrated = await InitialiseAsync(database);

        // The manual restore procedure: move the migrated file aside (never
        // delete it), copy the backup into place and check it.
        var failedFolder = Path.Combine(database.DirectoryPath, "failed");
        Directory.CreateDirectory(failedFolder);
        File.Move(database.DatabasePath, Path.Combine(failedFolder, "meeting-room-booking.db"));
        File.Copy(migrated.BackupPath!, database.DatabasePath);

        Assert.Equal("ok", await database.ScalarAsync("PRAGMA integrity_check"));
        Assert.Equal(MigrationTestDatabase.PrePhase1Migration, await database.ScalarAsync(LastMigrationSql));
        Assert.Equal(schemaBefore, await database.QueryAsync(schemaSql));
        Assert.Equal(roomsBefore, await database.QueryAsync(LegacySchemaFixture.SelectRoomsSql));
        Assert.Equal(bookingsBefore, await database.QueryAsync(LegacySchemaFixture.SelectBookingsSql));

        // The restored database migrates again, with a new backup, and keeps
        // its data.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var again = await InitialiseAsync(database);

        Assert.NotNull(again.BackupPath);
        Assert.NotEqual(migrated.BackupPath, again.BackupPath);
        Assert.Equal(bookingsBefore, await database.QueryAsync(LegacySchemaFixture.SelectBookingsSql));
    }

    private async Task<DatabaseInitializationResult> InitialiseAsync(
        MigrationTestDatabase database)
    {
        await using var context = database.CreateContext();

        return await DatabaseInitializer.InitialiseAsync(
            context,
            database.BackupDirectoryPath,
            clock,
            NullLogger.Instance);
    }

    private static SqliteConnection OpenReadOnly(
        string path)
    {
        var connection =
            new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());

        connection.Open();

        return connection;
    }
}
