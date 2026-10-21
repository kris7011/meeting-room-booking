using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRoomBooking.Api.Data.Migrations
{
    /// <summary>
    /// Records the AUTOINCREMENT counters before AddOrganizationsAndUsers
    /// rebuilds MeetingRooms and Bookings. SQLite does not document that a
    /// rebuild keeps them, and a counter that goes back would let a deleted
    /// booking's ID be handed out again. RestoreBookingInvariants puts them
    /// back and drops this table.
    /// </summary>
    public partial class CaptureSequenceState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "__MigrationSequenceSnapshot" AS
                SELECT "name", "seq"
                FROM sqlite_sequence
                WHERE "name" IN ('MeetingRooms', 'Bookings');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP TABLE IF EXISTS "__MigrationSequenceSnapshot";""");
        }
    }
}
