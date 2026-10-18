using MeetingRoomBooking.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MeetingRoomBooking.Api.Data.Migrations;

/// <summary>
/// Database-side invariant: even two requests that pass their application-level
/// checks concurrently cannot persist overlapping periods in the same room.
/// </summary>
[DbContext(typeof(BookingDbContext))]
[Migration("20261018141000_PreventBookingOverlap")]
public sealed class PreventBookingOverlap : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
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
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """DROP TRIGGER IF EXISTS "TR_Bookings_Overlap_Update";""");
        migrationBuilder.Sql(
            """DROP TRIGGER IF EXISTS "TR_Bookings_Overlap_Insert";""");
    }
}
