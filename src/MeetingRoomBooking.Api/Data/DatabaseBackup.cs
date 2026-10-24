using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MeetingRoomBooking.Api.Data;

/// <summary>
/// The row count of a table and an order-independent hash of its rows.
/// </summary>
public readonly record struct TableFingerprint(
    long Rows,
    ulong ContentHash);

/// <summary>
/// Copies the SQLite database from inside one read transaction and checks
/// the copy against that same snapshot before anything is migrated. The
/// original is only ever read.
/// </summary>
public static class DatabaseBackup
{
    // Not a table name: those starting with sqlite_ are not fingerprinted
    // as tables, apart from sqlite_sequence.
    private const string SchemaKey = "sqlite_schema";

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
    /// integrity check "ok", no foreign key violations, and the same schema
    /// and rows as the snapshot it was copied from. Throws if any check
    /// fails, and then leaves no backup file behind.
    /// </summary>
    /// <param name="label">
    /// Why the backup was taken, part of its file name
    /// (<c>&lt;database&gt;.&lt;label&gt;-&lt;UTC timestamp&gt;.db</c>), for example
    /// <c>pre-&lt;first pending migration&gt;</c> or <c>manual</c>.
    /// </param>
    /// <returns>The path of the verified backup.</returns>
    public static Task<string> CreateVerifiedAsync(
        string databaseFile,
        string? backupDirectory,
        string label,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return CreateVerifiedAsync(
            databaseFile,
            backupDirectory,
            label,
            timeProvider,
            afterSnapshot: null,
            cancellationToken);
    }

    /// <param name="afterSnapshot">
    /// For tests: runs once the snapshot has been fingerprinted and before it
    /// is copied, the moment at which a concurrent commit would make two
    /// separate reads of the database disagree.
    /// </param>
    internal static async Task<string> CreateVerifiedAsync(
        string databaseFile,
        string? backupDirectory,
        string label,
        TimeProvider timeProvider,
        Func<Task>? afterSnapshot,
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
                $".{label}" +
                $"-{timeProvider.GetUtcNow():yyyyMMdd'T'HHmmssfff'Z'}.db");

        // The copy gets the backup's name only once it is verified, so a
        // failed or interrupted backup never looks like a usable one, and
        // clean-up only ever deletes files that this call created.
        var temporaryPath =
            $"{backupPath}.partial-{Guid.NewGuid():N}.tmp";

        try
        {
            IReadOnlyDictionary<string, TableFingerprint> expected;

            await using (var source = Open(databaseFile, SqliteOpenMode.ReadOnly))
            {
                // A deferred transaction takes its snapshot at the first read
                // and keeps it until it ends, so the fingerprint and the copy
                // describe the same committed data, whatever other connections
                // commit meanwhile. VACUUM INTO cannot run inside a
                // transaction, which is why the backup API is used instead.
                await using var snapshot =
                    source.BeginTransaction(deferred: true);

                expected =
                    await FingerprintAsync(
                        source,
                        cancellationToken);

                if (afterSnapshot is not null)
                {
                    await afterSnapshot();
                }

                await using var copy =
                    Open(temporaryPath, SqliteOpenMode.ReadWriteCreate);

                // Copies the pages of this connection's snapshot.
                source.BackupDatabase(copy);

                // The copy inherits the source's WAL mode. A rollback journal
                // keeps the backup a single file, which read-only checks do
                // not add -wal and -shm files to.
                await SetJournalModeAsync(
                    copy,
                    "delete",
                    cancellationToken);
            }

            await using (var backup = Open(temporaryPath, SqliteOpenMode.ReadOnly))
            {
                var problem =
                    await FindIntegrityProblemAsync(
                        backup,
                        cancellationToken);

                if (problem is not null)
                {
                    throw new InvalidOperationException(
                        $"The backup of {databaseFile} failed its checks: {problem}. No backup was kept.");
                }

                var difference =
                    FindDifference(
                        expected,
                        await FingerprintAsync(
                            backup,
                            cancellationToken));

                if (difference is not null)
                {
                    throw new InvalidOperationException(
                        $"The backup of {databaseFile} does not match the snapshot it was copied from ({difference}). No backup was kept.");
                }
            }

            // Fails rather than replaces if a file of that name exists.
            File.Move(temporaryPath, backupPath);

            return backupPath;
        }
        finally
        {
            DeleteWithSidecars(temporaryPath);
        }
    }

    /// <summary>
    /// Null if <c>PRAGMA integrity_check</c> is "ok" and
    /// <c>PRAGMA foreign_key_check</c> finds nothing; otherwise what failed.
    /// </summary>
    public static async Task<string?> FindIntegrityProblemAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
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
                return $"integrity check reported \"{result}\"";
            }
        }

        await using (var foreignKeyCheck = connection.CreateCommand())
        {
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check";

            await using var reader =
                await foreignKeyCheck.ExecuteReaderAsync(cancellationToken);

            if (await reader.ReadAsync(cancellationToken))
            {
                return $"foreign key violation in table {reader.GetString(0)}";
            }
        }

        return null;
    }

    /// <summary>
    /// A fingerprint of every table (sqlite_* system tables excluded, apart
    /// from sqlite_sequence, which holds the AUTOINCREMENT counters) and, under
    /// "sqlite_schema", of the schema: tables, indexes and triggers. Equal
    /// fingerprints mean the same schema and the same rows, in whatever order
    /// the rows are stored.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, TableFingerprint>> FingerprintAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT "name" FROM sqlite_master
                WHERE "type" = 'table'
                  AND ("name" NOT LIKE 'sqlite_%' OR "name" = 'sqlite_sequence')
                ORDER BY "name"
                """;

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var fingerprints =
            new Dictionary<string, TableFingerprint>(StringComparer.Ordinal)
            {
                // rootpage is left out: where a table is stored is not part
                // of what it contains.
                [SchemaKey] =
                    await FingerprintRowsAsync(
                        connection,
                        """SELECT "type", "name", "tbl_name", "sql" FROM sqlite_master""",
                        cancellationToken)
            };

        foreach (var table in tables)
        {
            // Table names come from sqlite_master, not from user input.
            fingerprints[table] =
                await FingerprintRowsAsync(
                    connection,
                    $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"",
                    cancellationToken);
        }

        return fingerprints;
    }

    /// <summary>
    /// Null if both sets of fingerprints are equal; otherwise the first
    /// table that differs.
    /// </summary>
    public static string? FindDifference(
        IReadOnlyDictionary<string, TableFingerprint> expected,
        IReadOnlyDictionary<string, TableFingerprint> actual)
    {
        foreach (var name in expected.Keys
                     .Union(actual.Keys)
                     .Order(StringComparer.Ordinal))
        {
            var hasExpected = expected.TryGetValue(name, out var wanted);
            var hasActual = actual.TryGetValue(name, out var found);

            if (!hasExpected || !hasActual)
            {
                return $"table {name} exists in only one of them";
            }

            if (wanted.Rows != found.Rows)
            {
                return $"table {name} has {found.Rows} rows instead of {wanted.Rows}";
            }

            if (wanted.ContentHash != found.ContentHash)
            {
                return $"table {name} has the same number of rows but different contents";
            }
        }

        return null;
    }

    private static async Task<TableFingerprint> FingerprintRowsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.CommandText = sql;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8);

        long rows = 0;
        ulong contentHash = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.SetLength(0);

            for (var column = 0; column < reader.FieldCount; column++)
            {
                WriteValue(writer, reader.GetValue(column));
            }

            writer.Flush();

            // Summing the row hashes makes the result independent of row
            // order, which VACUUM and the backup API do not promise to keep.
            contentHash = unchecked(
                contentHash +
                BitConverter.ToUInt64(
                    SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))));

            rows++;
        }

        return new TableFingerprint(rows, contentHash);
    }

    // A type tag before each value, and a length before text and blobs, so
    // different rows never serialise to the same bytes.
    private static void WriteValue(
        BinaryWriter writer,
        object value)
    {
        switch (value)
        {
            case DBNull:
                writer.Write((byte)0);
                break;
            case long integer:
                writer.Write((byte)1);
                writer.Write(integer);
                break;
            case double real:
                writer.Write((byte)2);
                writer.Write(real);
                break;
            case string text:
                writer.Write((byte)3);
                writer.Write(text);
                break;
            case byte[] blob:
                writer.Write((byte)4);
                writer.Write(blob.Length);
                writer.Write(blob);
                break;
            default:
                throw new NotSupportedException(
                    $"Unexpected SQLite value of type {value.GetType()}.");
        }
    }

    private static async Task SetJournalModeAsync(
        SqliteConnection connection,
        string mode,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        // Not a parameter: PRAGMA does not accept bound values.
        command.CommandText = $"PRAGMA journal_mode = {mode}";

        var result =
            await command.ExecuteScalarAsync(cancellationToken) as string;

        if (!string.Equals(
                result,
                mode,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Could not switch the database copy to {mode} journal mode ({result}).");
        }
    }

    // Only ever called with a name this class generated, never with the
    // database or a finished backup. Best effort, so that a failure here
    // does not hide why the backup failed; a leftover is still recognisable
    // by its .partial-<id>.tmp name.
    private static void DeleteWithSidecars(
        string path)
    {
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // No pooling, so no handle stays open on either file. The source is
    // always opened read-only: a backup must never be able to change the
    // database it copies.
    private static SqliteConnection Open(
        string path,
        SqliteOpenMode mode)
    {
        var connection =
            new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = mode,
                    Pooling = false
                }.ToString());

        connection.Open();

        return connection;
    }
}
