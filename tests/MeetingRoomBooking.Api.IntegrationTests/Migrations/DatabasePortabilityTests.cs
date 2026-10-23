using MeetingRoomBooking.Api.Data;
using Microsoft.Data.Sqlite;

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
        Assert.Contains(".pre-manual-", Path.GetFileName(backup));
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
