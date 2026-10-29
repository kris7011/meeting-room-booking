using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRoomBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityLinkRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityLinkRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ClaimedProvider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ClaimedIssuer = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ClaimedSubject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ClaimedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimedVerifiedEmail = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    CompletedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CancelledUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityLinkRequests", x => x.Id);
                    table.CheckConstraint("CK_IdentityLinkRequests_Claim", "(\"ClaimedUtc\" IS NULL AND \"ClaimedProvider\" IS NULL AND \"ClaimedIssuer\" IS NULL AND \"ClaimedSubject\" IS NULL AND \"ClaimedVerifiedEmail\" IS NULL) OR (\"ClaimedUtc\" IS NOT NULL AND \"ClaimedProvider\" IN ('Entra', 'ExternalId') AND length(\"ClaimedIssuer\") > 0 AND length(\"ClaimedSubject\") > 0)");
                    table.CheckConstraint("CK_IdentityLinkRequests_CodeHash", "length(\"CodeHash\") = 64 AND \"CodeHash\" NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_IdentityLinkRequests_Expiry", "\"ExpiresUtc\" > \"CreatedUtc\"");
                    table.CheckConstraint("CK_IdentityLinkRequests_Outcome", "(\"CompletedUtc\" IS NULL OR \"ClaimedUtc\" IS NOT NULL) AND (\"CompletedUtc\" IS NULL OR \"CancelledUtc\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_IdentityLinkRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityLinkRequests_UserId",
                table: "IdentityLinkRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_IdentityLinkRequests_CodeHash",
                table: "IdentityLinkRequests",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_IdentityLinkRequests_PublicId",
                table: "IdentityLinkRequests",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityLinkRequests");
        }
    }
}
