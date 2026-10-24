using MeetingRoomBooking.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

public sealed class DatabasePortabilityTests
{
    [Fact]
    public async Task BackupAndRestore_PreserveRows_AndNeverModifyOriginal()
    {
        using var database = new MigrationTestDatabase();
        await database.MigrateToAsync();
        await database.ExecuteAsync(
            """CREATE TABLE "PortableTestRows" ("Id" INTEGER PRIMARY KEY, "Value" TEXT NOT NULL)""");
        await database.ExecuteAsync(
            """INSERT INTO "PortableTestRows" ("Id", "Value") VALUES (1, 'saved booking data')""");

        var originalChecksum = database.ComputeChecksum();

        var backup = await DatabasePortability.BackupAsync(
            database.DatabasePath,
            database.BackupDirectoryPath);

        Assert.True(File.Exists(backup));
        Assert.Matches(@"^meeting-room-booking\.manual-\d{8}T\d{9}Z\.db$", Path.GetFileName(backup));
        Assert.Equal(originalChecksum, database.ComputeChecksum());

        var restored = Path.Combine(database.DirectoryPath, "restored", "room-booking.db");
        await DatabasePortability.RestoreToNewAsync(backup, restored);

        Assert.True(File.Exists(restored));
        Assert.Equal(originalChecksum, database.ComputeChecksum());
        Assert.Equal(
            "ok",
            await ScalarAsync(restored, "PRAGMA integrity_check"));
        Assert.Equal(
            "saved booking data",
            await ScalarAsync(restored, """SELECT "Value" FROM "PortableTestRows" WHERE "Id" = 1"""));
        Assert.Equal(
            await ScalarAsync(backup, """SELECT COUNT(*) FROM "Memberships" """),
            await ScalarAsync(restored, """SELECT COUNT(*) FROM "Memberships" """));
    }

    [Fact]
    public async Task Backup_IncludesCommittedRowsThatAreStillOnlyInTheWal()
    {
        using var database = new MigrationTestDatabase();
        await database.MigrateToAsync();

        // A running API keeps its connection open, so recent commits can sit
        // in the -wal file and not yet in the main database file.
        await using var liveConnection = await database.OpenAsync();
        await ExecuteAsync(liveConnection, "PRAGMA journal_mode = WAL");
        await ExecuteAsync(liveConnection, "PRAGMA wal_autocheckpoint = 0");
        await ExecuteAsync(
            liveConnection,
            """CREATE TABLE "PortableTestRows" ("Id" INTEGER PRIMARY KEY, "Value" TEXT NOT NULL)""");
        await ExecuteAsync(
            liveConnection,
            """INSERT INTO "PortableTestRows" ("Id", "Value") VALUES (1, 'committed while the API runs')""");
        Assert.True(new FileInfo(database.DatabasePath + "-wal").Length > 0);

        var backup = await DatabasePortability.BackupAsync(database.DatabasePath, database.BackupDirectoryPath);
        var restored = Path.Combine(database.DirectoryPath, "restored", "room-booking.db");
        await DatabasePortability.RestoreToNewAsync(backup, restored);

        Assert.Equal(
            "committed while the API runs",
            await ScalarAsync(restored, """SELECT "Value" FROM "PortableTestRows" WHERE "Id" = 1"""));
    }

    [Fact]
    public async Task Restore_ProducesASingleWalModeFile_ThatTheApiStartsOnWithoutMigrating()
    {
        using var database = new MigrationTestDatabase();
        await database.MigrateToAsync();
        var backup = await DatabasePortability.BackupAsync(database.DatabasePath, database.BackupDirectoryPath);
        var restoredDirectory = Path.Combine(database.DirectoryPath, "restored");
        var restored = Path.Combine(restoredDirectory, "room-booking.db");

        await DatabasePortability.RestoreToNewAsync(backup, restored);

        // No temporary file or SQLite sidecar is left next to the new database.
        Assert.Equal(
            ["room-booking.db"],
            Directory.GetFiles(restoredDirectory).Select(Path.GetFileName));
        Assert.Equal("wal", await ScalarAsync(restored, "PRAGMA journal_mode"));

        // What the API runs at start-up: nothing to migrate, so no backup, and
        // the integrity, foreign-key and trigger checks pass.
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = restored, Pooling = false }.ToString())
            .Options;
        await using var context = new BookingDbContext(options);
        var result = await DatabaseInitializer.InitialiseAsync(
            context,
            Path.Combine(database.DirectoryPath, "startup-backups"),
            TimeProvider.System,
            NullLogger.Instance);

        Assert.Empty(result.AppliedMigrations);
        Assert.Null(result.BackupPath);
    }

    [Fact]
    public async Task Backup_RejectsAMissingDatabase_WithoutCreatingIt()
    {
        using var database = new MigrationTestDatabase();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => DatabasePortability.BackupAsync(database.DatabasePath, database.BackupDirectoryPath));

        Assert.False(File.Exists(database.DatabasePath));
        Assert.False(Directory.Exists(database.BackupDirectoryPath));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public async Task Restore_RefusesADestinationWithLeftoverSqliteSidecar(string suffix)
    {
        using var database = new MigrationTestDatabase();
        await database.MigrateToAsync();
        var backup = await DatabasePortability.BackupAsync(database.DatabasePath, database.BackupDirectoryPath);
        var destination = Path.Combine(database.DirectoryPath, "previous.db");
        await File.WriteAllTextAsync(destination + suffix, "belongs to an earlier database");

        await Assert.ThrowsAsync<IOException>(
            () => DatabasePortability.RestoreToNewAsync(backup, destination));

        Assert.False(File.Exists(destination));
        Assert.Equal("belongs to an earlier database", await File.ReadAllTextAsync(destination + suffix));
    }

    [Fact]
    public async Task Restore_NeverOverwritesAnExistingDatabase()
    {
        using var database = new MigrationTestDatabase();
        await database.MigrateToAsync();
        var backup = await DatabasePortability.BackupAsync(database.DatabasePath, database.BackupDirectoryPath);
        var destination = Path.Combine(database.DirectoryPath, "already-exists.db");
        await File.WriteAllTextAsync(destination, "DO NOT REPLACE");

        await Assert.ThrowsAsync<IOException>(
            () => DatabasePortability.RestoreToNewAsync(backup, destination));

        Assert.Equal("DO NOT REPLACE", await File.ReadAllTextAsync(destination));
        Assert.Equal("ok", await database.ScalarAsync("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task Restore_RejectsDamagedInput_WithoutCreatingDestination()
    {
        using var database = new MigrationTestDatabase();
        var corrupt = Path.Combine(database.DirectoryPath, "corrupt.db");
        var destination = Path.Combine(database.DirectoryPath, "should-not-exist.db");
        await File.WriteAllTextAsync(corrupt, "not a SQLite database");

        await Assert.ThrowsAnyAsync<Exception>(
            () => DatabasePortability.RestoreToNewAsync(corrupt, destination));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Restore_RejectsSourceEqualToDestination()
    {
        using var database = new MigrationTestDatabase();
        await database.MigrateToAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabasePortability.RestoreToNewAsync(database.DatabasePath, database.DatabasePath));

        Assert.Equal("ok", await database.ScalarAsync("PRAGMA integrity_check"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string file, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());

        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
