using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRoomBooking.Api.Data.Migrations
{
    /// <summary>
    /// Adds UserIdentities, Memberships, Invitations and AuditEvents, and the
    /// triggers that keep an organisation from losing its last Owner.
    /// </summary>
    /// <remarks>
    /// Reviewed by hand after scaffolding: only CREATE TABLE and CREATE INDEX,
    /// which SQLite supports without rebuilding a table, so MeetingRooms,
    /// Bookings and their overlap triggers are untouched. No rows are
    /// inserted: nobody becomes a member of any organisation.
    /// </remarks>
    public partial class AddIdentityAndMembershipTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrganizationId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OccurredUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrganizationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    InvitedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AcceptedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                    table.CheckConstraint("CK_Invitations_Accepted", "(\"AcceptedByUserId\" IS NULL) = (\"AcceptedUtc\" IS NULL)");
                    table.CheckConstraint("CK_Invitations_AcceptedOrRevoked", "\"AcceptedUtc\" IS NULL OR \"RevokedUtc\" IS NULL");
                    table.CheckConstraint("CK_Invitations_Email", "\"Email\" = lower(trim(\"Email\")) AND instr(\"Email\", '@') > 1");
                    table.CheckConstraint("CK_Invitations_Expiry", "\"ExpiresUtc\" > \"CreatedUtc\"");
                    table.CheckConstraint("CK_Invitations_Role", "\"Role\" IN ('Admin', 'Member')");
                    table.CheckConstraint("CK_Invitations_TokenHash", "length(\"TokenHash\") = 64 AND \"TokenHash\" NOT GLOB '*[^0-9a-f]*'");
                    table.ForeignKey(
                        name: "FK_Invitations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invitations_Users_AcceptedByUserId",
                        column: x => x.AcceptedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invitations_Users_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    OrganizationId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => new { x.OrganizationId, x.UserId });
                    table.CheckConstraint("CK_Memberships_Role", "\"Role\" IN ('Owner', 'Admin', 'Member')");
                    table.ForeignKey(
                        name: "FK_Memberships_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Memberships_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Memberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserIdentities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    VerifiedEmail = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserIdentities", x => x.Id);
                    table.CheckConstraint("CK_UserIdentities_IssuerAndSubject", "length(\"Issuer\") > 0 AND length(\"Subject\") > 0");
                    table.CheckConstraint("CK_UserIdentities_Provider", "\"Provider\" IN ('Entra', 'ExternalId')");
                    table.CheckConstraint("CK_UserIdentities_VerifiedEmail", "\"VerifiedEmail\" IS NULL OR (\"VerifiedEmail\" = lower(trim(\"VerifiedEmail\")) AND instr(\"VerifiedEmail\", '@') > 1)");
                    table.ForeignKey(
                        name: "FK_UserIdentities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorUserId",
                table: "AuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OrganizationId_OccurredUtc",
                table: "AuditEvents",
                columns: new[] { "OrganizationId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TargetUserId",
                table: "AuditEvents",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_AcceptedByUserId",
                table: "Invitations",
                column: "AcceptedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_InvitedByUserId",
                table: "Invitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "UX_Invitations_OrganizationId_Email_Open",
                table: "Invitations",
                columns: new[] { "OrganizationId", "Email" },
                unique: true,
                filter: "\"AcceptedUtc\" IS NULL AND \"RevokedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_CreatedByUserId",
                table: "Memberships",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_OrganizationId_Role",
                table: "Memberships",
                columns: new[] { "OrganizationId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_UserId",
                table: "Memberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_UserId",
                table: "UserIdentities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_VerifiedEmail",
                table: "UserIdentities",
                column: "VerifiedEmail");

            migrationBuilder.CreateIndex(
                name: "UX_UserIdentities_Issuer_Subject",
                table: "UserIdentities",
                columns: new[] { "Issuer", "Subject" },
                unique: true);

            // Once an organisation has an Owner, it keeps at least one: the
            // last Owner cannot be deleted, demoted or moved to another
            // organisation. Ownership is handed over by promoting someone
            // else first. An organisation without Owners (the legacy one
            // until it gets its first) is not affected.
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Memberships_LastOwner_Delete"
                BEFORE DELETE ON "Memberships"
                FOR EACH ROW
                WHEN OLD."Role" = 'Owner'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "Memberships" AS m
                      WHERE m."OrganizationId" = OLD."OrganizationId"
                        AND m."Role" = 'Owner'
                        AND m."UserId" <> OLD."UserId"
                  )
                BEGIN
                    SELECT RAISE(ABORT, 'last_owner');
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Memberships_LastOwner_Update"
                BEFORE UPDATE OF "Role", "OrganizationId" ON "Memberships"
                FOR EACH ROW
                WHEN OLD."Role" = 'Owner'
                  AND (NEW."Role" <> 'Owner' OR NEW."OrganizationId" <> OLD."OrganizationId")
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "Memberships" AS m
                      WHERE m."OrganizationId" = OLD."OrganizationId"
                        AND m."Role" = 'Owner'
                        AND m."UserId" <> OLD."UserId"
                  )
                BEGIN
                    SELECT RAISE(ABORT, 'last_owner');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "UserIdentities");
        }
    }
}
