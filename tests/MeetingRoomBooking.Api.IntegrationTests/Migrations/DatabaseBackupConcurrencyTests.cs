using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Bookings;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// Backups taken while another connection commits bookings, as a running
/// API does. Each test uses its own database in a temp folder.
/// </summary>
public sealed class DatabaseBackupConcurrencyTests
{
    private const string ConcurrentTitle = "Concurrent write";

    // Count and highest sequence number of the bookings made by these tests.
    // In a consistent snapshot they are exactly 0 .. count - 1.
    private const string ConcurrentBookingsSql =
        $"""
        SELECT COUNT(*), COALESCE(MAX(CAST("BookedBy" AS INTEGER)), -1)
        FROM "Bookings" WHERE "Title" = '{ConcurrentTitle}'
        """;

    private const string CountBookingsSql =
        """SELECT COUNT(*) FROM "Bookings" """;

    private readonly TestTimeProvider clock =
        new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Backups_TakenWhileBookingsAreCommitted_AreVerifiedSnapshots()
    {
        using var database = await CreateApiDatabaseAsync();

        await WhileBookingsAreCommittedAsync(database, async writer =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var committedBefore = writer.Committed;

                var backup = await DatabaseBackup.CreateVerifiedAsync(
                    database.DatabasePath,
                    database.BackupDirectoryPath,
                    "manual",
                    clock,
                    CancellationToken.None);

                var committedAfter = writer.Committed;
                clock.UtcNow = clock.UtcNow.AddSeconds(1);

                await AssertSnapshotWithinAsync(backup, committedBefore, committedAfter);
            }

            // Only verified backups are in the folder, no temporary files.
            Assert.Equal(10, Directory.GetFiles(database.BackupDirectoryPath).Length);
            Assert.All(
                Directory.GetFiles(database.BackupDirectoryPath),
                file => Assert.EndsWith(".db", file));
        });
    }

    [Fact]
    public async Task Backup_IsVerified_WhenABookingIsCommittedBetweenSnapshotAndCopy()
    {
        using var database = await CreateApiDatabaseAsync();
        var bookingsBefore = (long)(await database.ScalarAsync(CountBookingsSql))!;

        // The moment that made the previous implementation fail: its copy
        // would contain this booking, but its row counts would not.
        var backup = await DatabaseBackup.CreateVerifiedAsync(
            database.DatabasePath,
            database.BackupDirectoryPath,
            "manual",
            clock,
            afterSnapshot: () => CommitBookingAsync(database, sequence: 0),
            CancellationToken.None);

        Assert.Equal(bookingsBefore + 1, await database.ScalarAsync(CountBookingsSql));
        Assert.Equal(
            bookingsBefore,
            await new MigrationTestDatabase.ReadOnlyFile(backup).ScalarAsync(CountBookingsSql));
    }

    [Fact]
    public async Task Backup_LeavesOutABookingThatIsSavedButNotYetCommitted()
    {
        using var database = await CreateApiDatabaseAsync();
        await using var context = database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        // SaveChangesAsync sends the INSERT, but only CommitAsync makes it
        // visible to other connections, such as the backup's.
        context.Bookings.Add(NewBooking(sequence: 0));
        await context.SaveChangesAsync();

        var backup = await DatabaseBackup.CreateVerifiedAsync(
            database.DatabasePath,
            database.BackupDirectoryPath,
            "manual",
            clock,
            CancellationToken.None);

        await transaction.CommitAsync();

        Assert.Equal(
            0L,
            (await new MigrationTestDatabase.ReadOnlyFile(backup).QueryAsync(ConcurrentBookingsSql))[0][0]);
        Assert.Equal(
            1L,
            (await database.QueryAsync(ConcurrentBookingsSql))[0][0]);
    }

    [Fact]
    public async Task Restore_FromADatabaseThatIsBeingWritten_IsAVerifiedSnapshot()
    {
        using var database = await CreateApiDatabaseAsync();

        await WhileBookingsAreCommittedAsync(database, async writer =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var restored = Path.Combine(database.DirectoryPath, $"restored-{attempt}.db");
                var committedBefore = writer.Committed;

                await DatabasePortability.RestoreToNewAsync(database.DatabasePath, restored);

                await AssertSnapshotWithinAsync(restored, committedBefore, writer.Committed);
            }
        });
    }

    private static async Task<MigrationTestDatabase> CreateApiDatabaseAsync()
    {
        var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();

        // Like the API's database (EF Core creates it in WAL mode), so the
        // backup's reads and the writer's commits do not block each other.
        Assert.Equal("wal", await database.ScalarAsync("PRAGMA journal_mode"));

        return database;
    }

    /// <summary>
    /// Runs <paramref name="test"/> while a second connection keeps
    /// committing bookings, and checks that it committed some meanwhile.
    /// </summary>
    private static async Task WhileBookingsAreCommittedAsync(
        MigrationTestDatabase database,
        Func<BookingWriter, Task> test)
    {
        using var stop = new CancellationTokenSource();
        var writer = new BookingWriter(database);

        // Task.Run: Microsoft.Data.Sqlite's async methods complete
        // synchronously, so the loop would otherwise never yield.
        var writing = Task.Run(() => writer.RunAsync(stop.Token));

        try
        {
            await writer.WaitForCommitsAsync(1);
            var committedBefore = writer.Committed;

            await test(writer);

            // Proves the writer was not stalled while the test ran.
            await writer.WaitForCommitsAsync(committedBefore + 10);
        }
        finally
        {
            stop.Cancel();
            await writing;
        }
    }

    /// <summary>
    /// The copy holds exactly the writer's first n bookings, with n between
    /// the commits counted before and after copying: one point in time
    /// during the copy, not a mix of two.
    /// </summary>
    private static async Task AssertSnapshotWithinAsync(
        string copy,
        long committedBefore,
        long committedAfter)
    {
        var row = (await new MigrationTestDatabase.ReadOnlyFile(copy).QueryAsync(ConcurrentBookingsSql))[0];
        var count = (long)row[0]!;

        Assert.Equal(count - 1, (long)row[1]!);

        // The writer counts a commit only after CommitAsync returns, so one
        // more may already be in the copy.
        Assert.InRange(count, committedBefore, committedAfter + 1);
    }

    private static async Task CommitBookingAsync(
        MigrationTestDatabase database,
        long sequence)
    {
        await using var context = database.CreateContext();
        context.Bookings.Add(NewBooking(sequence));
        await context.SaveChangesAsync();
    }

    // One hour apart in the fixture's extra room, so they never overlap.
    private static Booking NewBooking(
        long sequence)
    {
        var start = LegacySchemaFixture.FirstStart.AddDays(1000).AddHours(sequence);

        return Booking.Create(
            organizationId: 1,
            roomId: LegacySchemaFixture.ExtraRoomId,
            title: ConcurrentTitle,
            bookedBy: sequence.ToString(),
            startUtc: start,
            endUtc: start.AddMinutes(30),
            createdUtc: start.AddDays(-1));
    }

    /// <summary>
    /// Commits one booking per transaction, the way BookingService does,
    /// until cancelled.
    /// </summary>
    private sealed class BookingWriter(
        MigrationTestDatabase database)
    {
        private long committed;

        public long Committed =>
            Interlocked.Read(ref committed);

        public async Task RunAsync(
            CancellationToken cancellationToken)
        {
            await using var context = database.CreateContext();

            while (!cancellationToken.IsCancellationRequested)
            {
                await using var transaction =
                    await context.Database.BeginTransactionAsync(CancellationToken.None);

                context.Bookings.Add(NewBooking(Committed));

                await context.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
                context.ChangeTracker.Clear();

                Interlocked.Increment(ref committed);
            }
        }

        public async Task WaitForCommitsAsync(
            long count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            while (Committed < count)
            {
                await Task.Delay(1, timeout.Token);
            }
        }
    }
}
