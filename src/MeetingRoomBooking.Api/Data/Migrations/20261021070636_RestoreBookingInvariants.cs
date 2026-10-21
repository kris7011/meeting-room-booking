using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRoomBooking.Api.Data.Migrations
{
    /// <summary>
    /// Restores what the table rebuilds in AddOrganizationsAndUsers can lose:
    /// the overlap triggers (SQLite drops a table's triggers with the table)
    /// and the AUTOINCREMENT counters captured by CaptureSequenceState.
    /// </summary>
    /// <remarks>
    /// The trigger SQL is copied from PreventBookingOverlap rather than shared
    /// with it, so that migration never changes.
    /// </remarks>
    public partial class RestoreBookingInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP TRIGGER IF EXISTS "TR_Bookings_Overlap_Insert";""");

            migrationBuilder.Sql("""
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
                END;
                """);

            migrationBuilder.Sql(
                """DROP TRIGGER IF EXISTS "TR_Bookings_Overlap_Update";""");

            migrationBuilder.Sql("""
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
                END;
                """);

            // A counter never goes back, so an ID that was ever used (also by
            // a booking deleted since) is never handed out again.
            migrationBuilder.Sql("""
                UPDATE sqlite_sequence
                SET "seq" = (
                    SELECT s."seq"
                    FROM "__MigrationSequenceSnapshot" AS s
                    WHERE s."name" = sqlite_sequence."name"
                )
                WHERE "seq" < (
                    SELECT s."seq"
                    FROM "__MigrationSequenceSnapshot" AS s
                    WHERE s."name" = sqlite_sequence."name"
                );
                """);

            // A rebuilt table that is empty may have lost its counter row.
            migrationBuilder.Sql("""
                INSERT INTO sqlite_sequence ("name", "seq")
                SELECT s."name", s."seq"
                FROM "__MigrationSequenceSnapshot" AS s
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM sqlite_sequence AS q
                    WHERE q."name" = s."name"
                );
                """);

            migrationBuilder.Sql(
                """DROP TABLE "__MigrationSequenceSnapshot";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Give CaptureSequenceState's Down() the table it expects. The
            // triggers stay: they belong to PreventBookingOverlap.
            migrationBuilder.Sql("""
                CREATE TABLE "__MigrationSequenceSnapshot" AS
                SELECT "name", "seq"
                FROM sqlite_sequence
                WHERE "name" IN ('MeetingRooms', 'Bookings');
                """);
        }
    }
}
