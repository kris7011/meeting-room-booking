using MeetingRoomBooking.Api.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// Phase 1, PR 2 migrates databases that are on the schema merged with
/// PR #7. It may only add tables: rooms, bookings, their IDs, counters and
/// overlap triggers must be exactly as before.
/// </summary>
public sealed class Pr7SchemaMigrationTests
{
    private const string SelectRoomsSql =
        """
        SELECT "Id", "OrganizationId", "Name", "Capacity", "ArchivedUtc"
        FROM "MeetingRooms" ORDER BY "Id"
        """;

    private const string SelectBookingsSql =
        """
        SELECT "Id", "OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc",
               "CreatedUtc", "CreatedByUserId", "UpdatedUtc", "UpdatedByUserId"
        FROM "Bookings" ORDER BY "Id"
        """;

    // The CREATE statements of the existing tables and their triggers and
    // indexes: none of them may change.
    private const string ExistingSchemaSql =
        """
        SELECT "type", "name", "sql" FROM sqlite_master
        WHERE "tbl_name" IN ('MeetingRooms', 'Bookings', 'Organizations', 'Users')
        ORDER BY "type", "name"
        """;

    private const string SequenceSql =
        """SELECT "name", "seq" FROM sqlite_sequence ORDER BY "name" """;

    [Fact]
    public async Task Migration_KeepsEverythingFromPr7Schema()
    {
        using var database = new MigrationTestDatabase();
        await CreatePr7DatabaseAsync(database);

        var rooms = await database.QueryAsync(SelectRoomsSql);
        var bookings = await database.QueryAsync(SelectBookingsSql);
        var schema = await database.QueryAsync(ExistingSchemaSql);
        var sequences = await database.QueryAsync(SequenceSql);

        await database.MigrateToAsync();

        Assert.Equal(rooms, await database.QueryAsync(SelectRoomsSql));
        Assert.Equal(bookings, await database.QueryAsync(SelectBookingsSql));
        Assert.Equal(schema, await database.QueryAsync(ExistingSchemaSql));
        Assert.Equal(sequences, await database.QueryAsync(SequenceSql));

        Assert.Equal(5, rooms.Count);
        Assert.Equal(4, bookings.Count);
        Assert.Empty(await database.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal("ok", await database.ScalarAsync("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task Migration_KeepsOverlapTriggersWorking()
    {
        using var database = new MigrationTestDatabase();
        await CreatePr7DatabaseAsync(database);

        await database.MigrateToAsync();

        var triggers =
            await database.QueryAsync(
                """
                SELECT "name", "sql" FROM sqlite_master
                WHERE "type" = 'trigger' AND "tbl_name" = 'Bookings'
                ORDER BY "name"
                """);

        Assert.Collection(
            triggers,
            trigger => Assert.Equal(
                MigrationTests.Normalize(MigrationTests.ExpectedInsertTriggerSql),
                MigrationTests.Normalize((string)trigger[1]!)),
            trigger => Assert.Equal(
                MigrationTests.Normalize(MigrationTests.ExpectedUpdateTriggerSql),
                MigrationTests.Normalize((string)trigger[1]!)));

        // Booking 1 occupies room 1 from 08:00 to 09:00.
        var start = LegacySchemaFixture.FirstStart;
        var overlap =
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                () => database.ExecuteAsync(
                    """
                    INSERT INTO "Bookings"
                        ("OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
                    VALUES (1, 1, 'Overlap', 'Eva', $start, $end, 0)
                    """,
                    ("$start", start.AddMinutes(15).UtcTicks),
                    ("$end", start.AddMinutes(45).UtcTicks)));
        Assert.Contains("booking_overlap", overlap.Message);
    }

    [Fact]
    public async Task Migration_GivesNobodyAccessAndAddsEmptyTables()
    {
        using var database = new MigrationTestDatabase();
        await CreatePr7DatabaseAsync(database);

        await database.MigrateToAsync();

        foreach (var table in new[] { "UserIdentities", "Memberships", "Invitations", "AuditEvents" })
        {
            Assert.Equal(
                0L,
                await database.ScalarAsync($"""SELECT COUNT(*) FROM "{table}" """));
        }

        Assert.Equal(
            ["TR_Memberships_LastOwner_Delete", "TR_Memberships_LastOwner_Update"],
            (await database.QueryAsync(
                """
                SELECT "name" FROM sqlite_master
                WHERE "type" = 'trigger' AND "tbl_name" = 'Memberships'
                ORDER BY "name"
                """)).Select(row => (string)row[0]!).ToArray());
    }

    [Fact]
    public async Task Startup_BacksUpPr7DatabaseBeforeMigrating()
    {
        using var database = new MigrationTestDatabase();
        await CreatePr7DatabaseAsync(database);
        var bookings = await database.QueryAsync(SelectBookingsSql);

        await using var context = database.CreateContext();
        var result =
            await DatabaseInitializer.InitialiseAsync(
                context,
                database.BackupDirectoryPath,
                new TestTimeProvider(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero)),
                NullLogger.Instance);

        Assert.Equal(
            MigrationTestDatabase.MigrationsAfter(MigrationTestDatabase.Pr7Migration),
            result.AppliedMigrations);
        Assert.Equal(
            $"meeting-room-booking.pre-{result.AppliedMigrations[0]}-20261022T080000000Z.db",
            Path.GetFileName(result.BackupPath));

        var backup = new MigrationTestDatabase.ReadOnlyFile(result.BackupPath!);
        Assert.Equal(
            MigrationTestDatabase.Pr7Migration,
            await backup.ScalarAsync(
                """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 1"""));
        Assert.Equal(bookings, await backup.QueryAsync(SelectBookingsSql));
        Assert.Equal(
            0L,
            await backup.ScalarAsync(
                """SELECT COUNT(*) FROM sqlite_master WHERE "name" = 'Memberships'"""));
    }

    /// <summary>
    /// The legacy fixture migrated to the PR #7 schema, plus what that schema
    /// allows: a user and a second organisation with its own room and booking.
    /// </summary>
    private static async Task CreatePr7DatabaseAsync(
        MigrationTestDatabase database)
    {
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync(MigrationTestDatabase.Pr7Migration);

        await database.ExecuteAsync(
            """
            INSERT INTO "Users" ("Id", "PublicId", "DisplayName", "CreatedUtc")
            VALUES (1, 'A1B2C3D4-0000-4000-8000-000000000001', 'Anna', 1)
            """);
        await database.ExecuteAsync(
            """
            INSERT INTO "Organizations" ("Id", "PublicId", "Name", "IsLegacy", "CreatedByUserId",
                                         "CreationRequestId", "CreationRequestHash", "CreatedUtc")
            VALUES (2, '6F1C1D4E-2B7A-4C55-9A0E-3D2B1F0A9C11', 'Acme', 0, 1,
                    'B7E5A0C2-1111-4222-8333-444455556666', $hash, 2)
            """,
            ("$hash", new string('a', 64)));
        await database.ExecuteAsync(
            """
            INSERT INTO "MeetingRooms" ("Id", "OrganizationId", "Name", "Capacity")
            VALUES (20, 2, 'Focus Room', 4)
            """);
        await database.ExecuteAsync(
            """
            INSERT INTO "Bookings"
                ("OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc", "CreatedByUserId")
            VALUES (2, 20, 'Acme planning', 'Anna', $start, $end, 3, 1)
            """,
            ("$start", LegacySchemaFixture.FirstStart.UtcTicks),
            ("$end", LegacySchemaFixture.FirstStart.AddHours(1).UtcTicks));
    }
}
