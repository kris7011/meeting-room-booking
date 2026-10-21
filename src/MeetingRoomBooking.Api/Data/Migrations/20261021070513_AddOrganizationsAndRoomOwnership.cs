using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRoomBooking.Api.Data.Migrations
{
    /// <summary>
    /// Adds Users and Organizations, creates the legacy organisation (ID 1)
    /// and moves every existing meeting room into it, unchanged and with the
    /// same ID. Bookings follow in AddBookingOrganization.
    /// </summary>
    /// <remarks>
    /// Reviewed by hand after scaffolding:
    /// <list type="bullet">
    /// <item>This runs before AddBookingOrganization because the bookings'
    /// new foreign key needs the unique (OrganizationId, Id) key on
    /// MeetingRooms to exist already. In one migration EF rebuilt Bookings
    /// first, which SQLite rejects as a foreign key mismatch.</item>
    /// <item>EF scaffolded DeleteData + InsertData for the three seeded rooms
    /// because their key now includes OrganizationId. That would delete rooms
    /// that bookings refer to and overwrite any edits to them, so it is
    /// removed: the rooms keep their rows and get OrganizationId 1 below.</item>
    /// <item>OrganizationId is added with the value 1 only to fill existing
    /// rows. The model has no default and the table rebuild that follows
    /// recreates MeetingRooms from the model, so no permanent database default
    /// remains (a test checks this). New rooms must name their organisation.</item>
    /// </list>
    /// </remarks>
    public partial class AddOrganizationsAndRoomOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MeetingRooms_Name",
                table: "MeetingRooms");

            migrationBuilder.AddColumn<long>(
                name: "ArchivedUtc",
                table: "MeetingRooms",
                type: "INTEGER",
                nullable: true);

            // Backfill only: existing rooms belong to the legacy organisation.
            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "MeetingRooms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_MeetingRooms_OrganizationId_Id",
                table: "MeetingRooms",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ContactEmail = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DisabledUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsLegacy = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreationRequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreationRequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                    table.CheckConstraint("CK_Organizations_CreationRequest", "(\"CreationRequestId\" IS NULL) = (\"CreationRequestHash\" IS NULL)");
                    table.CheckConstraint("CK_Organizations_LegacyHasNoCreator", "\"IsLegacy\" = 0 OR \"CreatedByUserId\" IS NULL");
                    table.CheckConstraint("CK_Organizations_Name", "length(\"Name\") BETWEEN 2 AND 100");
                    table.ForeignKey(
                        name: "FK_Organizations_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Organizations",
                columns: new[] { "Id", "CreatedByUserId", "CreatedUtc", "CreationRequestHash", "CreationRequestId", "DeletedUtc", "IsLegacy", "Name", "PublicId" },
                values: new object[] { 1, null, 639281376000000000L, null, null, null, true, "Legacy", new Guid("bf2b2b52-fb51-4871-b2d7-43d8e25a9e47") });

            migrationBuilder.CreateIndex(
                name: "UX_MeetingRooms_OrganizationId_Name",
                table: "MeetingRooms",
                columns: new[] { "OrganizationId", "Name" },
                unique: true,
                filter: "\"ArchivedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_CreatedByUserId_CreatedUtc",
                table: "Organizations",
                columns: new[] { "CreatedByUserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_Organizations_CreatedByUserId_CreationRequestId",
                table: "Organizations",
                columns: new[] { "CreatedByUserId", "CreationRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Organizations_IsLegacy",
                table: "Organizations",
                column: "IsLegacy",
                unique: true,
                filter: "\"IsLegacy\" = 1");

            migrationBuilder.CreateIndex(
                name: "UX_Organizations_PublicId",
                table: "Organizations",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Users_PublicId",
                table: "Users",
                column: "PublicId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MeetingRooms_Organizations_OrganizationId",
                table: "MeetingRooms",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MeetingRooms_Organizations_OrganizationId",
                table: "MeetingRooms");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MeetingRooms_OrganizationId_Id",
                table: "MeetingRooms");

            migrationBuilder.DropIndex(
                name: "UX_MeetingRooms_OrganizationId_Name",
                table: "MeetingRooms");

            migrationBuilder.DropColumn(
                name: "ArchivedUtc",
                table: "MeetingRooms");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "MeetingRooms");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRooms_Name",
                table: "MeetingRooms",
                column: "Name",
                unique: true);
        }
    }
}
