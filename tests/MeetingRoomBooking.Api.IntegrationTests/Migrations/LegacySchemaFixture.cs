namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// Builds a database on the schema in use before phase 1 and fills it with
/// raw SQL, so the fixture does not depend on today's entity model. The data
/// covers what a migration could get wrong: a gap in the room IDs, deleted
/// bookings above the highest remaining ID, quotes and emoji in titles, a
/// maximum-length name and exact UTC ticks.
/// </summary>
public static class LegacySchemaFixture
{
    public const int ExtraRoomId = 7;

    /// <summary>Bookings 4 and 5 are inserted and then deleted.</summary>
    public const long BookingSequenceBeforeMigration = 5;

    public const long RoomSequenceBeforeMigration = ExtraRoomId;

    public static readonly DateTimeOffset FirstStart =
        new(2031, 3, 10, 8, 0, 0, TimeSpan.Zero);

    public const string SelectRoomsSql =
        """
        SELECT "Id", "Name", "Capacity"
        FROM "MeetingRooms"
        ORDER BY "Id"
        """;

    public const string SelectBookingsSql =
        """
        SELECT "Id", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc"
        FROM "Bookings"
        ORDER BY "Id"
        """;

    public static async Task CreateAsync(
        MigrationTestDatabase database,
        bool withBookings = true)
    {
        await database.MigrateToAsync(
            MigrationTestDatabase.PrePhase1Migration);

        await database.ExecuteAsync(
            """
            INSERT INTO "MeetingRooms" ("Id", "Name", "Capacity")
            VALUES ($id, 'Ærø-rummet', 6)
            """,
            ("$id", ExtraRoomId));

        if (!withBookings)
        {
            return;
        }

        var createdUtc =
            new DateTimeOffset(2030, 12, 24, 23, 59, 59, TimeSpan.Zero)
                .AddTicks(1234567);

        var bookings = new (int RoomId, string Title, string BookedBy, DateTimeOffset Start, TimeSpan Length)[]
        {
            (1, "Weekly sync", "Anna", FirstStart, TimeSpan.FromHours(1)),
            (1, "It's \"quoted\" 🎉", "Bo", FirstStart.AddHours(1), TimeSpan.FromMinutes(30)),
            (ExtraRoomId, "Planlægning på Ærø", new string('x', 120), FirstStart.AddTicks(1), TimeSpan.FromMinutes(45)),
            (2, "Deleted later (4)", "Cecilie", FirstStart, TimeSpan.FromHours(2)),
            (3, "Deleted later (5)", "Dan", FirstStart, TimeSpan.FromHours(2))
        };

        foreach (var booking in bookings)
        {
            await database.ExecuteAsync(
                """
                INSERT INTO "Bookings"
                    ("RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
                VALUES ($room, $title, $bookedBy, $start, $end, $created)
                """,
                ("$room", booking.RoomId),
                ("$title", booking.Title),
                ("$bookedBy", booking.BookedBy),
                ("$start", booking.Start.UtcTicks),
                ("$end", booking.Start.Add(booking.Length).UtcTicks),
                ("$created", createdUtc.UtcTicks));
        }

        await database.ExecuteAsync(
            """DELETE FROM "Bookings" WHERE "Id" IN (4, 5)""");
    }

    /// <summary>
    /// A database whose Bookings table is empty but has handed out IDs:
    /// the case where a rebuilt table could lose its counter row.
    /// </summary>
    public static async Task CreateWithEmptyBookingsAsync(
        MigrationTestDatabase database)
    {
        await CreateAsync(database);

        await database.ExecuteAsync(
            """DELETE FROM "Bookings" """);
    }

    public static async Task<long?> GetSequenceAsync(
        MigrationTestDatabase database,
        string table)
    {
        var rows =
            await database.QueryAsync(
                $"""SELECT "seq" FROM sqlite_sequence WHERE "name" = '{table}'""");

        return rows.Count == 0
            ? null
            : (long?)rows[0][0];
    }
}
