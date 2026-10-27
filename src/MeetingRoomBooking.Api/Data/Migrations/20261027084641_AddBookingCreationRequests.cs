using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRoomBooking.Api.Data.Migrations
{
    /// <summary>
    /// Adds BookingCreationRequests: the Idempotency-Keys of booking
    /// creations, each with the booking it created (see ADR 0005).
    /// </summary>
    /// <remarks>
    /// Reviewed by hand after scaffolding: only CREATE TABLE and CREATE INDEX,
    /// so Bookings and its overlap triggers are untouched and no existing row
    /// changes. Deleting a booking sets its key's BookingId to NULL.
    /// </remarks>
    public partial class AddBookingCreationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingCreationRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrganizationId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BookingId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCreationRequests", x => x.Id);
                    table.CheckConstraint("CK_BookingCreationRequests_RequestHash", "length(\"RequestHash\") = 64 AND \"RequestHash\" NOT GLOB '*[^0-9a-f]*'");
                    table.ForeignKey(
                        name: "FK_BookingCreationRequests_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BookingCreationRequests_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingCreationRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingCreationRequests_BookingId",
                table: "BookingCreationRequests",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCreationRequests_CreatedUtc",
                table: "BookingCreationRequests",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCreationRequests_UserId",
                table: "BookingCreationRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_BookingCreationRequests_Organization_Key_Anonymous",
                table: "BookingCreationRequests",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true,
                filter: "\"UserId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BookingCreationRequests_Organization_User_Key",
                table: "BookingCreationRequests",
                columns: new[] { "OrganizationId", "UserId", "IdempotencyKey" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingCreationRequests");
        }
    }
}
