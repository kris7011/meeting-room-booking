using System.Text.RegularExpressions;
using MeetingRoomBooking.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// Phase 1 migrates a database on today's schema. Each test builds its own
/// database in a temp folder; none touches the development database.
/// </summary>
public sealed class MigrationTests
{
    internal const string ExpectedInsertTriggerSql =
        """
        CREATE TRIGGER "TR_Bookings_Overlap_Insert"
        BEFORE INSERT ON "Bookings"
        FOR EACH ROW
        WHEN EXISTS (
            SELECT 1
            FROM "Bookings" AS b
            WHERE b."RoomId" = NEW."RoomId"
              AND b."StartUtc" < NEW."EndUtc"
              AND b."EndUtc" > NEW."StartUtc"
        )
        BEGIN
            SELECT RAISE(ABORT, 'booking_overlap');
        END
        """;

    internal const string ExpectedUpdateTriggerSql =
        """
        CREATE TRIGGER "TR_Bookings_Overlap_Update"
        BEFORE UPDATE OF "RoomId", "StartUtc", "EndUtc" ON "Bookings"
        FOR EACH ROW
        WHEN EXISTS (
            SELECT 1
            FROM "Bookings" AS b
            WHERE b."RoomId" = NEW."RoomId"
              AND b."Id" <> NEW."Id"
              AND b."StartUtc" < NEW."EndUtc"
              AND b."EndUtc" > NEW."StartUtc"
        )
        BEGIN
            SELECT RAISE(ABORT, 'booking_overlap');
        END
        """;

    private const string InsertSecondOrganizationSql =
        """
        INSERT INTO "Organizations" ("Id", "PublicId", "Name", "IsLegacy", "CreatedUtc")
        VALUES (2, '6F1C1D4E-2B7A-4C55-9A0E-3D2B1F0A9C11', 'Acme', 0, 639281376000000000)
        """;

    [Fact]
    public async Task Migration_PreservesEveryRoomAndBooking()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);

        var roomsBefore =
            await database.QueryAsync(LegacySchemaFixture.SelectRoomsSql);
        var bookingsBefore =
            await database.QueryAsync(LegacySchemaFixture.SelectBookingsSql);

        await database.MigrateToAsync();

        Assert.Equal(4, roomsBefore.Count);
        Assert.Equal(3, bookingsBefore.Count);
        Assert.Equal(
            roomsBefore,
            await database.QueryAsync(LegacySchemaFixture.SelectRoomsSql));
        Assert.Equal(
            bookingsBefore,
            await database.QueryAsync(LegacySchemaFixture.SelectBookingsSql));
    }

    [Fact]
    public async Task Migration_PutsExistingDataInLegacyOrganisation()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);

        await database.MigrateToAsync();

        Assert.Equal(
            [[1L, null]],
            (await database.QueryAsync(
                """
                SELECT DISTINCT "OrganizationId", "ArchivedUtc"
                FROM "MeetingRooms"
                """)).ToArray());

        Assert.Equal(
            [[1L, null, null, null]],
            (await database.QueryAsync(
                """
                SELECT DISTINCT "OrganizationId", "CreatedByUserId", "UpdatedUtc", "UpdatedByUserId"
                FROM "Bookings"
                """)).ToArray());

        Assert.Equal(
            0L,
            await database.ScalarAsync("""SELECT COUNT(*) FROM "Users" """));
    }

    [Fact]
    public async Task LegacyOrganisation_ExistsExactlyOnce()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);

        await database.MigrateToAsync();

        var organizations =
            await database.QueryAsync(
                """
                SELECT "Id", "PublicId", "Name", "IsLegacy", "CreatedByUserId", "DeletedUtc"
                FROM "Organizations"
                """);

        var legacy = Assert.Single(organizations);
        Assert.Equal(
            [
                (long)LegacyOrganization.Id,
                LegacyOrganization.PublicId.ToString().ToUpperInvariant(),
                LegacyOrganization.Name,
                1L,
                null,
                null
            ],
            legacy);

        var exception =
            await Assert.ThrowsAsync<SqliteException>(
                () => database.ExecuteAsync(
                    """
                    INSERT INTO "Organizations" ("PublicId", "Name", "IsLegacy", "CreatedUtc")
                    VALUES ('0D5E8A43-63A9-4F1E-9C0A-9B1E4E2F7A10', 'Second legacy', 1, 0)
                    """));

        Assert.Contains("UNIQUE", exception.Message);
    }

    [Fact]
    public async Task Migration_NeverMovesSequencesBack()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);

        Assert.Equal(
            LegacySchemaFixture.BookingSequenceBeforeMigration,
            await LegacySchemaFixture.GetSequenceAsync(database, "Bookings"));

        await database.MigrateToAsync();

        Assert.Equal(
            LegacySchemaFixture.BookingSequenceBeforeMigration,
            await LegacySchemaFixture.GetSequenceAsync(database, "Bookings"));
        Assert.Equal(
            LegacySchemaFixture.RoomSequenceBeforeMigration,
            await LegacySchemaFixture.GetSequenceAsync(database, "MeetingRooms"));

        // IDs 4 and 5 were used by deleted bookings and must not come back.
        await database.ExecuteAsync(
            """
            INSERT INTO "Bookings"
                ("OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
            VALUES (1, 3, 'After migration', 'Eva', 1, 2, 0)
            """);

        Assert.Equal(
            LegacySchemaFixture.BookingSequenceBeforeMigration + 1,
            await database.ScalarAsync("""SELECT MAX("Id") FROM "Bookings" """));

        Assert.Null(
            await database.ScalarAsync(
                """SELECT name FROM sqlite_master WHERE name = '__MigrationSequenceSnapshot'"""));
    }

    [Fact]
    public async Task Migration_KeepsSequenceOfEmptyBookingsTable()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateWithEmptyBookingsAsync(database);

        await database.MigrateToAsync();

        Assert.Equal(
            0L,
            await database.ScalarAsync("""SELECT COUNT(*) FROM "Bookings" """));
        Assert.Equal(
            LegacySchemaFixture.BookingSequenceBeforeMigration,
            await LegacySchemaFixture.GetSequenceAsync(database, "Bookings"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Migration_KeepsOverlapTriggers(
        bool fromExistingDatabase)
    {
        using var database = new MigrationTestDatabase();

        if (fromExistingDatabase)
        {
            await LegacySchemaFixture.CreateAsync(database);
        }

        await database.MigrateToAsync();

        var triggers =
            await database.QueryAsync(
                """
                SELECT "name", "sql"
                FROM sqlite_master
                WHERE "type" = 'trigger'
                ORDER BY "name"
                """);

        Assert.Collection(
            triggers,
            trigger =>
            {
                Assert.Equal("TR_Bookings_Overlap_Insert", trigger[0]);
                Assert.Equal(Normalize(ExpectedInsertTriggerSql), Normalize((string)trigger[1]!));
            },
            trigger =>
            {
                Assert.Equal("TR_Bookings_Overlap_Update", trigger[0]);
                Assert.Equal(Normalize(ExpectedUpdateTriggerSql), Normalize((string)trigger[1]!));
            });
    }

    [Fact]
    public async Task OverlapTriggers_StillRejectOverlapAfterMigration()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();

        var start = LegacySchemaFixture.FirstStart;

        // Booking 1 occupies room 1 from 08:00 to 09:00.
        var insert =
            await Assert.ThrowsAsync<SqliteException>(
                () => database.ExecuteAsync(
                    """
                    INSERT INTO "Bookings"
                        ("OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
                    VALUES (1, 1, 'Overlap', 'Eva', $start, $end, 0)
                    """,
                    ("$start", start.AddMinutes(30).UtcTicks),
                    ("$end", start.AddMinutes(90).UtcTicks)));
        Assert.Contains("booking_overlap", insert.Message);

        // Booking 3 (room 7) cannot be moved onto booking 1.
        var update =
            await Assert.ThrowsAsync<SqliteException>(
                () => database.ExecuteAsync(
                    """UPDATE "Bookings" SET "RoomId" = 1 WHERE "Id" = 3"""));
        Assert.Contains("booking_overlap", update.Message);

        // Touching periods do not overlap: room 1 is free from 09:30.
        await database.ExecuteAsync(
            """
            INSERT INTO "Bookings"
                ("OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
            VALUES (1, 1, 'Adjacent', 'Eva', $start, $end, 0)
            """,
            ("$start", start.AddMinutes(90).UtcTicks),
            ("$end", start.AddMinutes(120).UtcTicks));
    }

    [Fact]
    public async Task Migration_LeavesNoForeignKeyOrIntegrityErrors()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);

        await database.MigrateToAsync();

        Assert.Empty(
            await database.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal(
            "ok",
            await database.ScalarAsync("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task CompositeForeignKey_RejectsCrossOrganisationBooking()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();
        await database.ExecuteAsync(InsertSecondOrganizationSql);

        // Room 3 belongs to the legacy organisation, not to organisation 2.
        var exception =
            await Assert.ThrowsAsync<SqliteException>(
                () => database.ExecuteAsync(
                    """
                    INSERT INTO "Bookings"
                        ("OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
                    VALUES (2, 3, 'Wrong organisation', 'Eva', 1, 2, 0)
                    """));

        Assert.Contains("FOREIGN KEY", exception.Message);
    }

    [Fact]
    public async Task RoomNames_AreUniquePerOrganisation()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();
        await database.ExecuteAsync(InsertSecondOrganizationSql);

        await database.ExecuteAsync(
            """
            INSERT INTO "MeetingRooms" ("OrganizationId", "Name", "Capacity")
            VALUES (2, 'Focus Room', 4)
            """);

        var duplicate =
            await Assert.ThrowsAsync<SqliteException>(
                () => database.ExecuteAsync(
                    """
                    INSERT INTO "MeetingRooms" ("OrganizationId", "Name", "Capacity")
                    VALUES (2, 'Focus Room', 6)
                    """));
        Assert.Contains("UNIQUE", duplicate.Message);

        // Archiving a room frees its name.
        await database.ExecuteAsync(
            """
            UPDATE "MeetingRooms" SET "ArchivedUtc" = 1
            WHERE "OrganizationId" = 2 AND "Name" = 'Focus Room'
            """);
        await database.ExecuteAsync(
            """
            INSERT INTO "MeetingRooms" ("OrganizationId", "Name", "Capacity")
            VALUES (2, 'Focus Room', 6)
            """);
    }

    [Fact]
    public async Task OrganizationId_HasNoDatabaseDefault()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();

        var tableSql =
            await database.QueryAsync(
                """
                SELECT "sql" FROM sqlite_master
                WHERE "type" = 'table' AND "name" IN ('MeetingRooms', 'Bookings')
                """);

        Assert.Equal(2, tableSql.Count);
        Assert.All(
            tableSql,
            row => Assert.DoesNotContain(
                "DEFAULT",
                (string)row[0]!,
                StringComparison.OrdinalIgnoreCase));

        // A row that does not name its organisation is rejected instead of
        // silently landing in the legacy organisation.
        var room =
            await Assert.ThrowsAsync<SqliteException>(
                () => database.ExecuteAsync(
                    """
                    INSERT INTO "MeetingRooms" ("Name", "Capacity")
                    VALUES ('No organisation', 4)
                    """));
        Assert.Contains("NOT NULL", room.Message);

        var booking =
            await Assert.ThrowsAsync<SqliteException>(
                () => database.ExecuteAsync(
                    """
                    INSERT INTO "Bookings"
                        ("RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
                    VALUES (3, 'No organisation', 'Eva', 1, 2, 0)
                    """));
        Assert.Contains("NOT NULL", booking.Message);
    }

    [Fact]
    public async Task Migration_IsNoOpWhenRunTwice()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();

        const string schemaSql =
            """SELECT "type", "name", "sql" FROM sqlite_master ORDER BY "type", "name" """;

        var schemaBefore = await database.QueryAsync(schemaSql);
        var bookingsBefore = await database.QueryAsync(LegacySchemaFixture.SelectBookingsSql);

        await database.MigrateToAsync();

        Assert.Equal(schemaBefore, await database.QueryAsync(schemaSql));
        Assert.Equal(bookingsBefore, await database.QueryAsync(LegacySchemaFixture.SelectBookingsSql));
    }

    [Fact]
    public async Task NewDatabase_GetsLegacyOrganisationAndOnlyItsDemoRooms()
    {
        using var database = new MigrationTestDatabase();

        await database.MigrateToAsync();

        Assert.Equal(
            [
                [1L, "Focus Room", 4L, 1L],
                [2L, "Collaboration Room", 8L, 1L],
                [3L, "Board Room", 12L, 1L]
            ],
            (await database.QueryAsync(
                """
                SELECT "Id", "Name", "Capacity", "OrganizationId"
                FROM "MeetingRooms" ORDER BY "Id"
                """)).ToArray());

        Assert.Equal(
            1L,
            await database.ScalarAsync("""SELECT COUNT(*) FROM "Organizations" """));
        Assert.Equal(
            0L,
            await database.ScalarAsync("""SELECT COUNT(*) FROM "Bookings" """));
    }

    [Fact]
    public async Task NewOrganisation_InheritsNoRoomsOrBookings()
    {
        using var database = new MigrationTestDatabase();
        await LegacySchemaFixture.CreateAsync(database);
        await database.MigrateToAsync();

        await database.ExecuteAsync(InsertSecondOrganizationSql);

        Assert.Equal(
            0L,
            await database.ScalarAsync(
                """SELECT COUNT(*) FROM "MeetingRooms" WHERE "OrganizationId" = 2"""));
        Assert.Equal(
            0L,
            await database.ScalarAsync(
                """SELECT COUNT(*) FROM "Bookings" WHERE "OrganizationId" = 2"""));
    }

    [Fact]
    public async Task Model_HasNoPendingChanges()
    {
        using var database = new MigrationTestDatabase();
        await using var context = database.CreateContext();

        Assert.False(
            context.Database.HasPendingModelChanges());
    }

    internal static string Normalize(
        string sql)
    {
        return Regex.Replace(sql, @"\s+", " ")
            .Trim()
            .TrimEnd(';');
    }
}
