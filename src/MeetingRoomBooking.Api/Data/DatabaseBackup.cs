using Microsoft.Data.Sqlite;

namespace MeetingRoomBooking.Api.Data;

/// <summary>
/// Copies the SQLite database with VACUUM INTO, which writes a consistent
/// snapshot to a new file and leaves the original untouched, and checks the
/// copy before anything is migrated.
/// </summary>
public static class DatabaseBackup
{
    /// <summary>
    /// The full path of the database file a connection string opens, or null
    /// for an in-memory database.
    /// </summary>
    public static string? ResolveDatabaseFile(
        string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder =
            new SqliteConnectionStringBuilder(connectionString);

        if (builder.Mode == SqliteOpenMode.Memory ||
            string.IsNullOrWhiteSpace(builder.DataSource) ||
            builder.DataSource.Equals(
                ":memory:",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(builder.DataSource);
    }

    /// <summary>
    /// Writes a backup of <paramref name="databaseFile"/> and verifies it:
    /// integrity check "ok" and the same number of rows in every table.
    /// Throws if either fails; the original is only ever read.
    /// </summary>
    /// <returns>The path of the verified backup.</returns>
    public static async Task<string> CreateVerifiedAsync(
        string databaseFile,
        string? backupDirectory,
        string firstPendingMigration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var directory =
            string.IsNullOrWhiteSpace(backupDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(databaseFile)!,
                    "backups")
                : Path.GetFullPath(backupDirectory);

        Directory.CreateDirectory(directory);

        var backupPath =
            Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(databaseFile)}" +
                $".pre-{firstPendingMigration}" +
                $"-{timeProvider.GetUtcNow():yyyyMMdd'T'HHmmssfff'Z'}.db");

        IReadOnlyDictionary<string, long> sourceCounts;

        await using (var source = Open(databaseFile))
        {
            await using (var vacuum = source.CreateCommand())
            {
                // The path is bound as a parameter, never spliced into SQL.
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.AddWithValue("$path", backupPath);

                await vacuum.ExecuteNonQueryAsync(cancellationToken);
            }

            sourceCounts =
                await CountRowsAsync(
                    source,
                    cancellationToken);
        }

        await using var backup =
            Open(backupPath);

        var integrity =
            await ExecuteScalarAsync(
                backup,
                "PRAGMA integrity_check",
                cancellationToken);

        if (!string.Equals(
                integrity as string,
                "ok",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The database backup {backupPath} failed its integrity check ({integrity}). Nothing was migrated.");
        }

        var backupCounts =
            await CountRowsAsync(
                backup,
                cancellationToken);

        if (!sourceCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(
                    backupCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The database backup {backupPath} does not have the same rows as the database. Nothing was migrated.");
        }

        return backupPath;
    }

    /// <summary>Row count per table, sqlite_* system tables excluded.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> CountRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT "name" FROM sqlite_master
                WHERE "type" = 'table' AND "name" NOT LIKE 'sqlite_%'
                ORDER BY "name"
                """;

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in tables)
        {
            // Table names come from sqlite_master, not from user input.
            counts[table] =
                (long)(await ExecuteScalarAsync(
                    connection,
                    $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\"",
                    cancellationToken))!;
        }

        return counts;
    }

    // Read-only: a backup must never be able to change the database it
    // copies. No pooling, so no handle stays open on either file.
    private static SqliteConnection Open(
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

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
