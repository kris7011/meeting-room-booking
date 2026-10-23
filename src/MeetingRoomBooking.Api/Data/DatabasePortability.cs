using Microsoft.Data.Sqlite;

namespace MeetingRoomBooking.Api.Data;

/// <summary>
/// Explicit, portable SQLite backups and safe restore into a NEW database.
/// Existing databases are never replaced by this utility.
/// </summary>
public static class DatabasePortability
{
    public static Task<string> BackupAsync(
        string databaseFile,
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(databaseFile);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The source database does not exist.", source);
        }

        // Reuse the existing snapshot + integrity and per-table-count checks.
        // The name clearly distinguishes manual backups from migration backups.
        return DatabaseBackup.CreateVerifiedAsync(
            source,
            Path.GetFullPath(backupDirectory),
            "manual",
            TimeProvider.System,
            cancellationToken);
    }

    /// <summary>
    /// Restores an independently verifiable SQLite snapshot into a new file.
    /// SQLite's backup API includes committed WAL transactions if present.
    /// The destination is never overwritten, even when it is damaged.
    /// </summary>
    public static async Task RestoreToNewAsync(
        string backupFile,
        string destinationFile,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(backupFile);
        var destinationPath = Path.GetFullPath(destinationFile);

        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source and destination must be different files.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The backup file does not exist.", sourcePath);
        }

        // Never overwrite a database, including an existing SQLite WAL pair.
        if (File.Exists(destinationPath) ||
            File.Exists(destinationPath + "-wal") ||
            File.Exists(destinationPath + "-shm") ||
            Directory.Exists(destinationPath))
        {
            throw new IOException("The destination already exists. Choose a NEW database filename.");
        }

        await using var source = Open(sourcePath, SqliteOpenMode.ReadOnly);
        await VerifyAsync(source, cancellationToken);
        var expected = await DatabaseBackup.CountRowsAsync(source, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = destinationPath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var temporary = Open(temporaryPath, SqliteOpenMode.ReadWriteCreate))
            {
                // SQLite snapshots the source correctly even in WAL mode.
                source.BackupDatabase(temporary);
            }

            await using (var copy = Open(temporaryPath, SqliteOpenMode.ReadOnly))
            {
                await VerifyAsync(copy, cancellationToken);
                var actual = await DatabaseBackup.CountRowsAsync(copy, cancellationToken);
                if (!expected.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .SequenceEqual(actual.OrderBy(x => x.Key, StringComparer.Ordinal)))
                {
                    throw new InvalidOperationException("Restored database table counts differ from the backup.");
                }
            }

            // Atomic on the same volume. Move fails if another process created
            // the destination in the meantime; it never replaces that file.
            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(temporaryPath + "-wal")) File.Delete(temporaryPath + "-wal");
            if (File.Exists(temporaryPath + "-shm")) File.Delete(temporaryPath + "-shm");
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task VerifyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(await integrity.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SQLite integrity check failed. The backup was not restored.");
            }
        }

        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            await using var rows = await foreignKeys.ExecuteReaderAsync(cancellationToken);
            if (await rows.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("SQLite foreign-key check failed. The backup was not restored.");
            }
        }
    }
}
