using System.Security.Cryptography;
using MeetingRoomBooking.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// A SQLite database in its own new folder under the system temp folder,
/// deleted again on dispose. Every connection uses an explicit connection
/// string to that file, so a migration test can never reach the
/// development database.
/// </summary>
public sealed class MigrationTestDatabase : IDisposable
{
    /// <summary>The last migration before phase 1.</summary>
    public const string PrePhase1Migration =
        "20261018141000_PreventBookingOverlap";

    /// <summary>The last migration of phase 1, PR 1 (merged as PR #7).</summary>
    public const string Pr7Migration =
        "20261021070636_RestoreBookingInvariants";

    private static readonly IReadOnlyList<string> AllMigrations =
        LoadMigrations();

    /// <summary>The newest migration in the code.</summary>
    public static string LatestMigration =>
        AllMigrations[^1];

    /// <summary>The migrations that follow <paramref name="migration"/>, in order.</summary>
    public static IReadOnlyList<string> MigrationsAfter(
        string migration)
    {
        return AllMigrations
            .SkipWhile(id => id != migration)
            .Skip(1)
            .ToArray();
    }

    private static IReadOnlyList<string> LoadMigrations()
    {
        var options =
            new DbContextOptionsBuilder<BookingDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options;

        using var context = new BookingDbContext(options);

        return context.Database.GetMigrations().ToArray();
    }

    public MigrationTestDatabase()
    {
        DirectoryPath =
            Path.Combine(
                Path.GetTempPath(),
                $"meeting-room-booking-migration-tests-{Guid.NewGuid():N}");

        Directory.CreateDirectory(DirectoryPath);

        DatabasePath =
            Path.Combine(
                DirectoryPath,
                "meeting-room-booking.db");

        BackupDirectoryPath =
            Path.Combine(
                DirectoryPath,
                "backups");

        TestDatabaseGuard.EnsureIsolated(DatabasePath);
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public string BackupDirectoryPath { get; }

    /// <summary>
    /// Pooling is off so no connection keeps the file open between steps
    /// (a restore replaces the file). Foreign keys are on, as EF Core turns
    /// them on for its own connections.
    /// </summary>
    public string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString();

    public BookingDbContext CreateContext()
    {
        var options =
            new DbContextOptionsBuilder<BookingDbContext>()
                .UseSqlite(ConnectionString)
                .Options;

        return new BookingDbContext(options);
    }

    public async Task MigrateToAsync(
        string? targetMigration = null)
    {
        await using var context =
            CreateContext();

        await context.GetService<IMigrator>()
            .MigrateAsync(targetMigration);
    }

    public async Task<int> ExecuteAsync(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection =
            await OpenAsync();

        await using var command =
            connection.CreateCommand();

        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(
                name,
                value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Rows as arrays of raw SQLite values (long, string, double, null).</summary>
    public async Task<IReadOnlyList<object?[]>> QueryAsync(
        string sql)
    {
        await using var connection =
            await OpenAsync();

        return await QueryAsync(
            connection,
            sql);
    }

    public static async Task<IReadOnlyList<object?[]>> QueryAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command =
            connection.CreateCommand();

        command.CommandText = sql;

        await using var reader =
            await command.ExecuteReaderAsync();

        var rows = new List<object?[]>();

        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];

            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] =
                    reader.IsDBNull(index)
                        ? null
                        : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<object?> ScalarAsync(
        string sql)
    {
        var rows =
            await QueryAsync(sql);

        return rows.Count == 0
            ? null
            : rows[0][0];
    }

    public string ComputeChecksum()
    {
        return ComputeChecksum(DatabasePath);
    }

    public static string ComputeChecksum(
        string path)
    {
        using var stream =
            File.OpenRead(path);

        return Convert.ToHexString(
            SHA256.HashData(stream));
    }

    /// <summary>Reads another SQLite file, such as a backup, without changing it.</summary>
    public sealed class ReadOnlyFile(
        string path)
    {
        private readonly string connectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

        public async Task<IReadOnlyList<object?[]>> QueryAsync(
            string sql)
        {
            await using var connection =
                new SqliteConnection(connectionString);

            await connection.OpenAsync();

            return await MigrationTestDatabase.QueryAsync(
                connection,
                sql);
        }

        public async Task<object?> ScalarAsync(
            string sql)
        {
            var rows =
                await QueryAsync(sql);

            return rows.Count == 0
                ? null
                : rows[0][0];
        }
    }

    public async Task<SqliteConnection> OpenAsync()
    {
        var connection =
            new SqliteConnection(ConnectionString);

        await connection.OpenAsync();

        return connection;
    }

    public void Dispose()
    {
        const int maximumAttempts = 5;

        for (var attempt = 1;
            attempt <= maximumAttempts;
            attempt++)
        {
            if (!Directory.Exists(DirectoryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(
                    DirectoryPath,
                    recursive: true);

                return;
            }
            catch (IOException)
                when (attempt < maximumAttempts)
            {
                Thread.Sleep(
                    millisecondsTimeout:
                        attempt * 50);
            }
        }
    }
}
