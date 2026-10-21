using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRoomBooking.Api.Data.Migrations
{
    /// <summary>
    /// Moves every existing booking, unchanged and with the same ID, into the
    /// legacy organisation and ties each booking to its room's organisation
    /// with a foreign key on (OrganizationId, RoomId).
    /// </summary>
    /// <remarks>
    /// Reviewed by hand after scaffolding: OrganizationId is added with the
    /// value 1 only to fill existing rows. The rebuild of Bookings recreates
    /// the table from the model, which has no default, so a booking that does
    /// not name its organisation is rejected (a test checks this). The rebuild
    /// also drops the overlap triggers; RestoreBookingInvariants recreates
    /// them.
    /// </remarks>
    public partial class AddBookingOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_MeetingRooms_RoomId",
                table: "Bookings");

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Bookings",
                type: "INTEGER",
                nullable: true);

            // Backfill only: existing bookings belong to the legacy organisation.
            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "Bookings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "Bookings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UpdatedUtc",
                table: "Bookings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CreatedByUserId",
                table: "Bookings",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_OrganizationId_RoomId",
                table: "Bookings",
                columns: new[] { "OrganizationId", "RoomId" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_OrganizationId_StartUtc",
                table: "Bookings",
                columns: new[] { "OrganizationId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_UpdatedByUserId",
                table: "Bookings",
                column: "UpdatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_MeetingRooms_OrganizationId_RoomId",
                table: "Bookings",
                columns: new[] { "OrganizationId", "RoomId" },
                principalTable: "MeetingRooms",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Users_CreatedByUserId",
                table: "Bookings",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Users_UpdatedByUserId",
                table: "Bookings",
                column: "UpdatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_MeetingRooms_OrganizationId_RoomId",
                table: "Bookings");

            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_Users_CreatedByUserId",
                table: "Bookings");

            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_Users_UpdatedByUserId",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_CreatedByUserId",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_OrganizationId_RoomId",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_OrganizationId_StartUtc",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_UpdatedByUserId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "UpdatedUtc",
                table: "Bookings");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_MeetingRooms_RoomId",
                table: "Bookings",
                column: "RoomId",
                principalTable: "MeetingRooms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Rebuilding Bookings above drops the overlap triggers that
            // PreventBookingOverlap created; put them back. Rolling back in
            // production is done by restoring the backup, not by Down().
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
        }
    }
}
