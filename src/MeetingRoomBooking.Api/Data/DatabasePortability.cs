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

        // The same verified snapshot the API takes before migrating.
        // "manual" distinguishes these from the API's "pre-<migration>" backups.
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

        // One snapshot for the checks, the fingerprint and the copy, as in
        // DatabaseBackup, in case the source is a database still in use.
        await using var snapshot = source.BeginTransaction(deferred: true);
        await VerifyAsync(source, cancellationToken);
        var expected = await DatabaseBackup.FingerprintAsync(source, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = destinationPath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var temporary = Open(temporaryPath, SqliteOpenMode.ReadWriteCreate))
            {
                // SQLite snapshots the source correctly even in WAL mode.
                source.BackupDatabase(temporary);

                // Backups use a rollback journal (see DatabaseBackup), and the
                // backup API copies that. The API's own databases use WAL (EF
                // Core creates them so), which lets reads continue while a
                // booking is written.
                await SetWalJournalModeAsync(temporary, cancellationToken);
            }
            // Closing the last connection checkpointed and removed the WAL, so
            // the single temporary file holds every page that is moved below.

            await using (var copy = Open(temporaryPath, SqliteOpenMode.ReadOnly))
            {
                await VerifyAsync(copy, cancellationToken);
                var difference = DatabaseBackup.FindDifference(
                    expected,
                    await DatabaseBackup.FingerprintAsync(copy, cancellationToken));
                if (difference is not null)
                {
                    throw new InvalidOperationException($"The restored database differs from the backup ({difference}).");
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

    private static async Task SetWalJournalModeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode = WAL";
        var result = await journalMode.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.Equals(result, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not switch the restored database to WAL mode ({result}).");
        }
    }

    private static async Task VerifyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var problem = await DatabaseBackup.FindIntegrityProblemAsync(connection, cancellationToken);
        if (problem is not null)
        {
            throw new InvalidOperationException($"SQLite check failed: {problem}. The backup was not restored.");
        }
    }
}
