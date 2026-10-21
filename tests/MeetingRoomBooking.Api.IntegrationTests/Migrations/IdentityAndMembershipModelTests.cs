using MeetingRoomBooking.Api.Features.Audit;
using MeetingRoomBooking.Api.Features.Organizations;
using MeetingRoomBooking.Api.Features.Users;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// What the database itself guarantees for identities, memberships,
/// invitations and audit events. Each test uses its own migrated database
/// in a temp folder and writes rows with raw SQL, as no endpoint can yet.
/// </summary>
public sealed class IdentityAndMembershipModelTests : IDisposable
{
    private const string EntraIssuerA =
        "https://login.microsoftonline.com/11111111-1111-4111-8111-111111111111/v2.0";

    private const string EntraIssuerB =
        "https://login.microsoftonline.com/22222222-2222-4222-8222-222222222222/v2.0";

    private const string ExternalIdIssuer =
        "https://33333333-3333-4333-8333-333333333333.ciamlogin.com/33333333-3333-4333-8333-333333333333/v2.0";

    private const int Anna = 1, Bo = 2, Cecilie = 3;
    private const int Acme = 2, Globex = 3;

    private static readonly long Created =
        new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero).UtcTicks;

    private static readonly long InSevenDays =
        new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero).UtcTicks;

    private readonly MigrationTestDatabase database = new();

    public void Dispose()
    {
        database.Dispose();
    }

    // UserIdentities

    [Fact]
    public async Task User_CanHaveSeveralIdentities()
    {
        await SeedAsync();

        await InsertIdentityAsync(Anna, "Entra", EntraIssuerA, "oid-anna", "anna@acme.example");
        await InsertIdentityAsync(Anna, "ExternalId", ExternalIdIssuer, "oid-anna-mail", "anna@acme.example");

        Assert.Equal(
            2L,
            await database.ScalarAsync("""SELECT COUNT(*) FROM "UserIdentities" WHERE "UserId" = 1"""));
    }

    [Fact]
    public async Task Identity_IsUniquePerIssuerAndSubject()
    {
        await SeedAsync();
        await InsertIdentityAsync(Anna, "Entra", EntraIssuerA, "oid-1", null);

        var duplicate =
            await Assert.ThrowsAsync<SqliteException>(
                () => InsertIdentityAsync(Bo, "Entra", EntraIssuerA, "oid-1", null));
        Assert.Contains("UNIQUE", duplicate.Message);

        // The same object ID in another tenant is another person.
        await InsertIdentityAsync(Bo, "Entra", EntraIssuerB, "oid-1", null);
    }

    [Fact]
    public async Task SameVerifiedEmail_NeverLinksTwoUsers()
    {
        await SeedAsync();

        await InsertIdentityAsync(Anna, "Entra", EntraIssuerA, "oid-anna", "shared@example.com");
        await InsertIdentityAsync(Bo, "ExternalId", ExternalIdIssuer, "oid-bo", "shared@example.com");

        Assert.Equal(
            2L,
            await database.ScalarAsync(
                """
                SELECT COUNT(DISTINCT "UserId") FROM "UserIdentities"
                WHERE "VerifiedEmail" = 'shared@example.com'
                """));
    }

    [Fact]
    public async Task Identity_RejectsInvalidValues()
    {
        await SeedAsync();

        await AssertRejectedAsync(
            "CHECK",
            () => InsertIdentityAsync(Anna, "Google", EntraIssuerA, "oid", null));
        await AssertRejectedAsync(
            "CHECK",
            () => InsertIdentityAsync(Anna, "Entra", EntraIssuerA, "", null));
        await AssertRejectedAsync(
            "CHECK",
            () => InsertIdentityAsync(Anna, "Entra", "", "oid", null));
        await AssertRejectedAsync(
            "CHECK",
            () => InsertIdentityAsync(Anna, "Entra", EntraIssuerA, "oid", "Anna@Acme.example"));
        await AssertRejectedAsync(
            "FOREIGN KEY",
            () => InsertIdentityAsync(99, "Entra", EntraIssuerA, "oid", null));
    }

    // Memberships

    [Fact]
    public async Task User_CanBelongToSeveralOrganisations()
    {
        await SeedAsync();

        await InsertMembershipAsync(Acme, Anna, "Owner");
        await InsertMembershipAsync(Globex, Anna, "Member");

        Assert.Equal(
            [[(long)Acme, "Owner"], [(long)Globex, "Member"]],
            (await database.QueryAsync(
                """
                SELECT "OrganizationId", "Role" FROM "Memberships"
                WHERE "UserId" = 1 ORDER BY "OrganizationId"
                """)).ToArray());
    }

    [Fact]
    public async Task Membership_IsUniquePerUserAndOrganisation()
    {
        await SeedAsync();
        await InsertMembershipAsync(Acme, Anna, "Owner");

        await AssertRejectedAsync(
            "UNIQUE",
            () => InsertMembershipAsync(Acme, Anna, "Member"));
    }

    [Fact]
    public async Task Membership_RejectsInvalidRoleAndRelations()
    {
        await SeedAsync();

        await AssertRejectedAsync("CHECK", () => InsertMembershipAsync(Acme, Anna, "Superuser"));
        await AssertRejectedAsync("FOREIGN KEY", () => InsertMembershipAsync(99, Anna, "Member"));
        await AssertRejectedAsync("FOREIGN KEY", () => InsertMembershipAsync(Acme, 99, "Member"));
    }

    [Fact]
    public async Task LastOwner_CannotBeRemovedDemotedOrMoved()
    {
        await SeedAsync();
        await InsertMembershipAsync(Acme, Anna, "Owner");
        await InsertMembershipAsync(Acme, Bo, "Admin");

        await AssertRejectedAsync(
            "last_owner",
            () => database.ExecuteAsync("""DELETE FROM "Memberships" WHERE "OrganizationId" = 2 AND "UserId" = 1"""));
        await AssertRejectedAsync(
            "last_owner",
            () => database.ExecuteAsync("""UPDATE "Memberships" SET "Role" = 'Admin' WHERE "OrganizationId" = 2 AND "UserId" = 1"""));
        await AssertRejectedAsync(
            "last_owner",
            () => database.ExecuteAsync("""UPDATE "Memberships" SET "OrganizationId" = 3 WHERE "OrganizationId" = 2 AND "UserId" = 1"""));

        Assert.Equal(
            "Owner",
            await database.ScalarAsync("""SELECT "Role" FROM "Memberships" WHERE "OrganizationId" = 2 AND "UserId" = 1"""));
    }

    [Fact]
    public async Task Ownership_CanBeHandedOver()
    {
        await SeedAsync();
        await InsertMembershipAsync(Acme, Anna, "Owner");
        await InsertMembershipAsync(Acme, Bo, "Admin");

        // Promote first, then the old Owner can step down and leave.
        await database.ExecuteAsync("""UPDATE "Memberships" SET "Role" = 'Owner' WHERE "OrganizationId" = 2 AND "UserId" = 2""");
        await database.ExecuteAsync("""UPDATE "Memberships" SET "Role" = 'Member' WHERE "OrganizationId" = 2 AND "UserId" = 1""");
        await database.ExecuteAsync("""DELETE FROM "Memberships" WHERE "OrganizationId" = 2 AND "UserId" = 1""");

        await AssertRejectedAsync(
            "last_owner",
            () => database.ExecuteAsync("""DELETE FROM "Memberships" WHERE "OrganizationId" = 2 AND "UserId" = 2"""));
    }

    [Fact]
    public async Task LastOwnerRule_IsPerOrganisation()
    {
        await SeedAsync();
        await InsertMembershipAsync(Acme, Anna, "Owner");
        await InsertMembershipAsync(Acme, Bo, "Owner");
        await InsertMembershipAsync(Globex, Bo, "Owner");

        // Bo can leave Acme, which keeps Anna, but not Globex, where Bo is
        // the only Owner, even though Bo owns another organisation.
        await database.ExecuteAsync("""DELETE FROM "Memberships" WHERE "OrganizationId" = 2 AND "UserId" = 2""");

        await AssertRejectedAsync(
            "last_owner",
            () => database.ExecuteAsync("""DELETE FROM "Memberships" WHERE "OrganizationId" = 3 AND "UserId" = 2"""));
    }

    [Fact]
    public async Task MembersAndAdmins_CanAlwaysBeRemoved_AlsoWithoutAnOwner()
    {
        await SeedAsync();

        // The legacy organisation has no Owner yet; that is allowed.
        await InsertMembershipAsync(1, Cecilie, "Member");
        await InsertMembershipAsync(Acme, Anna, "Owner");
        await InsertMembershipAsync(Acme, Bo, "Admin");
        await InsertMembershipAsync(Acme, Cecilie, "Member");

        await database.ExecuteAsync("""DELETE FROM "Memberships" WHERE "UserId" = 3""");
        await database.ExecuteAsync("""DELETE FROM "Memberships" WHERE "OrganizationId" = 2 AND "UserId" = 2""");

        Assert.Equal(
            1L,
            await database.ScalarAsync("""SELECT COUNT(*) FROM "Memberships" """));
    }

    // Invitations

    [Fact]
    public async Task Invitation_StoresOnlyTheTokenHash()
    {
        await SeedAsync();
        var (token, hash) = InvitationToken.Create();

        await AssertRejectedAsync(
            "CHECK",
            () => InsertInvitationAsync(Acme, "new@example.com", "Member", token));

        await InsertInvitationAsync(Acme, "new@example.com", "Member", hash);

        var stored = (string?)await database.ScalarAsync("""SELECT "TokenHash" FROM "Invitations" """);
        Assert.Equal(InvitationToken.Hash(token), stored);
        Assert.NotEqual(token, stored);
        Assert.Equal(
            0L,
            await database.ScalarAsync(
                $"""SELECT COUNT(*) FROM "Invitations" WHERE instr("TokenHash", '{token}') > 0"""));
    }

    [Fact]
    public async Task Invitation_TokenHashIsUnique()
    {
        await SeedAsync();
        var (_, hash) = InvitationToken.Create();
        await InsertInvitationAsync(Acme, "a@example.com", "Member", hash);

        await AssertRejectedAsync(
            "UNIQUE",
            () => InsertInvitationAsync(Globex, "b@example.com", "Member", hash));
    }

    [Fact]
    public async Task OnlyOneOpenInvitation_PerEmailAndOrganisation()
    {
        await SeedAsync();
        await InsertInvitationAsync(Acme, "new@example.com", "Member", NewHash());

        await AssertRejectedAsync(
            "UNIQUE",
            () => InsertInvitationAsync(Acme, "new@example.com", "Admin", NewHash()));

        // Another organisation may invite the same address.
        await InsertInvitationAsync(Globex, "new@example.com", "Member", NewHash());

        // Once revoked, Acme can invite the address again.
        await database.ExecuteAsync(
            """UPDATE "Invitations" SET "RevokedUtc" = $now WHERE "OrganizationId" = 2""",
            ("$now", Created + 1));
        await InsertInvitationAsync(Acme, "new@example.com", "Admin", NewHash());
    }

    [Fact]
    public async Task Invitation_RejectsInvalidValues()
    {
        await SeedAsync();

        await AssertRejectedAsync("CHECK", () => InsertInvitationAsync(Acme, "x@example.com", "Owner", NewHash()));
        await AssertRejectedAsync("CHECK", () => InsertInvitationAsync(Acme, "X@Example.com", "Member", NewHash()));
        await AssertRejectedAsync("CHECK", () => InsertInvitationAsync(Acme, "no-at-sign", "Member", NewHash()));
        await AssertRejectedAsync("CHECK", () => InsertInvitationAsync(Acme, "x@example.com", "Member", NewHash(), expires: Created));
        await AssertRejectedAsync("CHECK", () => InsertInvitationAsync(Acme, "x@example.com", "Member", NewHash(), acceptedUtc: Created + 1));
        await AssertRejectedAsync("CHECK", () => InsertInvitationAsync(Acme, "x@example.com", "Member", NewHash(), acceptedBy: Bo, acceptedUtc: Created + 1, revokedUtc: Created + 2));
        await AssertRejectedAsync("FOREIGN KEY", () => InsertInvitationAsync(99, "x@example.com", "Member", NewHash()));
        await AssertRejectedAsync("FOREIGN KEY", () => InsertInvitationAsync(Acme, "x@example.com", "Member", NewHash(), invitedBy: 99));
        await AssertRejectedAsync("FOREIGN KEY", () => InsertInvitationAsync(Acme, "x@example.com", "Member", NewHash(), acceptedBy: 99, acceptedUtc: Created + 1));
    }

    // AuditEvents

    [Fact]
    public async Task AuditEvent_RecordsOrganisationActorTypeAndTime()
    {
        await SeedAsync();

        await InsertAuditEventAsync(Acme, Anna, Bo, "MembershipRoleChanged");
        await InsertAuditEventAsync(null, Anna, null, "OrganizationCreated");

        Assert.Equal(
            [[(long)Acme, (long)Anna, (long)Bo, "MembershipRoleChanged", Created]],
            (await database.QueryAsync(
                """
                SELECT "OrganizationId", "ActorUserId", "TargetUserId", "Type", "OccurredUtc"
                FROM "AuditEvents" WHERE "OrganizationId" IS NOT NULL
                """)).ToArray());

        await AssertRejectedAsync("FOREIGN KEY", () => InsertAuditEventAsync(99, Anna, null, "OrganizationRenamed"));
        await AssertRejectedAsync("FOREIGN KEY", () => InsertAuditEventAsync(Acme, 99, null, "OrganizationRenamed"));
    }

    // The EF model reads what the schema stores, including the enums.

    [Fact]
    public async Task Model_ReadsRowsWrittenWithTheSchema()
    {
        await SeedAsync();
        await InsertIdentityAsync(Anna, "ExternalId", ExternalIdIssuer, "oid-anna", "anna@acme.example");
        await InsertMembershipAsync(Acme, Anna, "Owner");
        await InsertInvitationAsync(Acme, "bo@example.com", "Admin", NewHash());
        await InsertAuditEventAsync(Acme, Anna, null, "InvitationCreated");

        await using var context = database.CreateContext();

        var identity = await context.UserIdentities.SingleAsync();
        Assert.Equal(IdentityProvider.ExternalId, identity.Provider);
        Assert.Equal("anna@acme.example", identity.VerifiedEmail);

        var membership = await context.Memberships.SingleAsync();
        Assert.Equal((Acme, Anna, MembershipRole.Owner), (membership.OrganizationId, membership.UserId, membership.Role));

        var invitation = await context.Invitations.SingleAsync();
        Assert.Equal(MembershipRole.Admin, invitation.Role);
        Assert.Equal(new DateTimeOffset(Created, TimeSpan.Zero), invitation.CreatedUtc);

        var auditEvent = await context.AuditEvents.SingleAsync();
        Assert.Equal(AuditEventType.InvitationCreated, auditEvent.Type);
    }

    [Fact]
    public void InvitationToken_HashIsLowerCaseHexSha256()
    {
        var (token, hash) = InvitationToken.Create();
        var (otherToken, _) = InvitationToken.Create();

        Assert.Equal(43, token.Length);
        Assert.DoesNotContain('=', token);
        Assert.NotEqual(token, otherToken);
        Assert.Equal(InvitationToken.HashLength, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, InvitationToken.Hash(token));
    }

    private async Task SeedAsync()
    {
        await database.MigrateToAsync();

        await database.ExecuteAsync(
            """
            INSERT INTO "Users" ("Id", "PublicId", "DisplayName", "CreatedUtc") VALUES
                (1, 'A1B2C3D4-0000-4000-8000-000000000001', 'Anna', 1),
                (2, 'A1B2C3D4-0000-4000-8000-000000000002', 'Bo', 1),
                (3, 'A1B2C3D4-0000-4000-8000-000000000003', 'Cecilie', 1)
            """);

        await database.ExecuteAsync(
            """
            INSERT INTO "Organizations" ("Id", "PublicId", "Name", "IsLegacy", "CreatedByUserId", "CreatedUtc") VALUES
                (2, '6F1C1D4E-2B7A-4C55-9A0E-3D2B1F0A9C11', 'Acme', 0, 1, 2),
                (3, '6F1C1D4E-2B7A-4C55-9A0E-3D2B1F0A9C12', 'Globex', 0, 2, 2)
            """);
    }

    private Task InsertIdentityAsync(
        int userId,
        string provider,
        string issuer,
        string subject,
        string? verifiedEmail)
    {
        return database.ExecuteAsync(
            """
            INSERT INTO "UserIdentities" ("UserId", "Provider", "Issuer", "Subject", "VerifiedEmail", "CreatedUtc")
            VALUES ($user, $provider, $issuer, $subject, $email, $created)
            """,
            ("$user", userId),
            ("$provider", provider),
            ("$issuer", issuer),
            ("$subject", subject),
            ("$email", verifiedEmail),
            ("$created", Created));
    }

    private Task InsertMembershipAsync(
        int organizationId,
        int userId,
        string role)
    {
        return database.ExecuteAsync(
            """
            INSERT INTO "Memberships" ("OrganizationId", "UserId", "Role", "CreatedUtc")
            VALUES ($org, $user, $role, $created)
            """,
            ("$org", organizationId),
            ("$user", userId),
            ("$role", role),
            ("$created", Created));
    }

    private Task InsertInvitationAsync(
        int organizationId,
        string email,
        string role,
        string tokenHash,
        int invitedBy = Anna,
        long? expires = null,
        int? acceptedBy = null,
        long? acceptedUtc = null,
        long? revokedUtc = null)
    {
        return database.ExecuteAsync(
            """
            INSERT INTO "Invitations"
                ("OrganizationId", "Email", "Role", "TokenHash", "InvitedByUserId", "CreatedUtc",
                 "ExpiresUtc", "AcceptedByUserId", "AcceptedUtc", "RevokedUtc")
            VALUES ($org, $email, $role, $hash, $invitedBy, $created, $expires, $acceptedBy, $acceptedUtc, $revokedUtc)
            """,
            ("$org", organizationId),
            ("$email", email),
            ("$role", role),
            ("$hash", tokenHash),
            ("$invitedBy", invitedBy),
            ("$created", Created),
            ("$expires", expires ?? InSevenDays),
            ("$acceptedBy", acceptedBy),
            ("$acceptedUtc", acceptedUtc),
            ("$revokedUtc", revokedUtc));
    }

    private Task InsertAuditEventAsync(
        int? organizationId,
        int? actorUserId,
        int? targetUserId,
        string type)
    {
        return database.ExecuteAsync(
            """
            INSERT INTO "AuditEvents" ("OrganizationId", "ActorUserId", "TargetUserId", "Type", "OccurredUtc")
            VALUES ($org, $actor, $target, $type, $occurred)
            """,
            ("$org", organizationId),
            ("$actor", actorUserId),
            ("$target", targetUserId),
            ("$type", type),
            ("$occurred", Created));
    }

    private static string NewHash()
    {
        return InvitationToken.Create().Hash;
    }

    private static async Task AssertRejectedAsync(
        string expectedMessagePart,
        Func<Task> write)
    {
        var exception =
            await Assert.ThrowsAsync<SqliteException>(write);

        Assert.Contains(expectedMessagePart, exception.Message);
    }
}
