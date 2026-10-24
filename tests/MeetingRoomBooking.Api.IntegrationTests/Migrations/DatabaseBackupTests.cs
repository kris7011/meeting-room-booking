using MeetingRoomBooking.Api.Data;
using Microsoft.Data.Sqlite;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// What a backup checks, and what is left on disk when it fails.
/// </summary>
public sealed class DatabaseBackupTests
{
    private readonly TestTimeProvider clock =
        new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task FailedBackup_LeavesNoFile_AndKeepsUnrelatedFilesAndTheDatabase()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();

        // A booking for a room that does not exist, written with foreign keys
        // off: the copy is faithful, so it fails foreign_key_check.
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA foreign_keys = OFF;
                INSERT INTO "Bookings" ("OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
                VALUES (1, 999, 'Orphan', 'Test', 1, 2, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        Directory.CreateDirectory(database.BackupDirectoryPath);
        var earlierBackup = Path.Combine(database.BackupDirectoryPath, "meeting-room-booking.manual-20261023T120000000Z.db");
        var unrelated = Path.Combine(database.BackupDirectoryPath, "meeting-room-booking.manual-20261024T120000000Z.db.notes.txt");
        await File.WriteAllTextAsync(earlierBackup, "an earlier backup");
        await File.WriteAllTextAsync(unrelated, "someone's notes");
        var checksumBefore = database.ComputeChecksum();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseBackup.CreateVerifiedAsync(
                database.DatabasePath,
                database.BackupDirectoryPath,
                "manual",
                clock,
                CancellationToken.None));

        Assert.Contains("foreign key violation in table Bookings", exception.Message);
        Assert.Equal(
            [earlierBackup, unrelated],
            Directory.GetFiles(database.BackupDirectoryPath).Order(StringComparer.Ordinal));
        Assert.Equal("an earlier backup", await File.ReadAllTextAsync(earlierBackup));
        Assert.Equal("someone's notes", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(checksumBefore, database.ComputeChecksum());
    }

    [Fact]
    public async Task Backup_NeverReplacesAFileWithTheSameName()
    {
        using var database = new MigrationTestDatabase();
        await database.MigrateToAsync();
        Directory.CreateDirectory(database.BackupDirectoryPath);
        var taken = Path.Combine(database.BackupDirectoryPath, "meeting-room-booking.manual-20261024T120000000Z.db");
        await File.WriteAllTextAsync(taken, "DO NOT REPLACE");

        await Assert.ThrowsAsync<IOException>(
            () => DatabaseBackup.CreateVerifiedAsync(
                database.DatabasePath,
                database.BackupDirectoryPath,
                "manual",
                clock,
                CancellationToken.None));

        Assert.Equal("DO NOT REPLACE", await File.ReadAllTextAsync(taken));
        Assert.Equal([taken], Directory.GetFiles(database.BackupDirectoryPath));
    }

    [Fact]
    public async Task Backup_IsASingleRollbackJournalFile_WithTheSameFingerprint()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();

        var backup = await DatabaseBackup.CreateVerifiedAsync(
            database.DatabasePath,
            database.BackupDirectoryPath,
            "manual",
            clock,
            CancellationToken.None);

        Assert.Equal("delete", await new MigrationTestDatabase.ReadOnlyFile(backup).ScalarAsync("PRAGMA journal_mode"));
        Assert.Equal([backup], Directory.GetFiles(database.BackupDirectoryPath));

        await using var source = await database.OpenAsync();
        await using var copy = Open(backup);
        var expected = await DatabaseBackup.FingerprintAsync(source, CancellationToken.None);

        Assert.Null(DatabaseBackup.FindDifference(expected, await DatabaseBackup.FingerprintAsync(copy, CancellationToken.None)));

        // Schema, every EF Core table and the AUTOINCREMENT counters.
        Assert.Contains("sqlite_schema", expected.Keys);
        Assert.Contains("sqlite_sequence", expected.Keys);
        Assert.Contains("Bookings", expected.Keys);
        Assert.Contains("__EFMigrationsHistory", expected.Keys);
    }

    [Fact]
    public async Task Fingerprint_IgnoresRowOrder_ButNotContents()
    {
        using var database = new MigrationTestDatabase();
        await using var connection = await database.OpenAsync();

        async Task<IReadOnlyDictionary<string, TableFingerprint>> FingerprintAfterAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""DROP TABLE IF EXISTS "Rows"; CREATE TABLE "Rows" ("Value"); {sql}""";
            await command.ExecuteNonQueryAsync();
            return await DatabaseBackup.FingerprintAsync(connection, CancellationToken.None);
        }

        var original = await FingerprintAfterAsync("""INSERT INTO "Rows" VALUES (1), ('a'), (NULL), (x'01'), (2.5)""");
        var reordered = await FingerprintAfterAsync("""INSERT INTO "Rows" VALUES (2.5), (x'01'), (NULL), ('a'), (1)""");
        var changed = await FingerprintAfterAsync("""INSERT INTO "Rows" VALUES (1), ('b'), (NULL), (x'01'), (2.5)""");
        var retyped = await FingerprintAfterAsync("""INSERT INTO "Rows" VALUES ('1'), ('a'), (NULL), (x'01'), (2.5)""");

        Assert.Null(DatabaseBackup.FindDifference(original, reordered));
        Assert.Equal(
            "table Rows has the same number of rows but different contents",
            DatabaseBackup.FindDifference(original, changed));
        Assert.NotNull(DatabaseBackup.FindDifference(original, retyped));
    }

    private static SqliteConnection Open(
        string path)
    {
        var connection = new SqliteConnection(
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
