using System.Net;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Organizations;
using MeetingRoomBooking.Api.IntegrationTests.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;

namespace MeetingRoomBooking.Api.IntegrationTests.Organizations;

/// <summary>
/// Who gets into an organisation: the configured first Owner of the legacy
/// organisation, and people its Owners and Admins invite. Nobody else.
/// </summary>
public sealed class OrganizationAccessTests
    : IDisposable
{
    private static readonly string LegacyPath =
        $"/api/organizations/{LegacyOrganization.PublicId}/invitations";

    private readonly MeetingRoomBookingApiFactory baseFactory =
        new();

    private readonly WebApplicationFactory<Program> factory;

    private readonly string ownerToken =
        TokenFor("oid-owner", "owner@example.com", name: "Olivia Owner");

    private readonly string inviteeToken =
        TokenFor("oid-invitee", "invitee@example.com", name: "Ivan Invitee");

    public OrganizationAccessTests()
    {
        factory = baseFactory.WithWebHostBuilder(builder => builder.UseEntraTestTokens());
    }

    public void Dispose()
    {
        factory.Dispose();
        baseFactory.Dispose();
    }

    // First Owner of the legacy organisation

    [Fact]
    public async Task ConfiguredRegisteredUser_BecomesTheFirstLegacyOwner()
    {
        var owner = await RegisterUserAsync(factory, ownerToken);

        Assert.Equal(LegacyOwnerBootstrapOutcome.OwnerAdded, await BootstrapAsync(owner.ToString()));

        using var me = await GetMeAsync(factory, ownerToken);
        var organization = Assert.Single((await JsonAsync(me)).GetProperty("user").GetProperty("organizations").EnumerateArray());
        Assert.Equal(LegacyOrganization.PublicId, organization.GetProperty("publicId").GetGuid());
        Assert.Equal("Owner", organization.GetProperty("role").GetString());
        Assert.True(organization.GetProperty("isLegacy").GetBoolean());

        // Recorded as done by the system, not by a user.
        Assert.Equal(
            1L,
            await CountAsync(
                factory,
                """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'MembershipAdded' AND "OrganizationId" = 1 AND "ActorUserId" IS NULL"""));
    }

    [Fact]
    public async Task Bootstrap_RunsAtStartUpFromConfiguration()
    {
        var owner = await RegisterUserAsync(factory, ownerToken);

        using var restarted =
            baseFactory.WithWebHostBuilder(
                builder =>
                {
                    builder.UseEntraTestTokens();
                    builder.UseSetting(LegacyOwnerBootstrap.SettingName, owner.ToString());
                });

        using var me = await GetMeAsync(restarted, ownerToken);
        var organization = Assert.Single((await JsonAsync(me)).GetProperty("user").GetProperty("organizations").EnumerateArray());
        Assert.Equal("Owner", organization.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Bootstrap_NeverReplacesOrAddsToAnExistingOwner()
    {
        var owner = await RegisterUserAsync(factory, ownerToken);
        var other = await RegisterUserAsync(factory, inviteeToken);

        await BootstrapAsync(owner.ToString());

        Assert.Equal(LegacyOwnerBootstrapOutcome.AlreadyOwner, await BootstrapAsync(owner.ToString()));
        Assert.Equal(LegacyOwnerBootstrapOutcome.OwnedByAnotherUser, await BootstrapAsync(other.ToString()));

        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "Memberships" """));
    }

    [Fact]
    public async Task Bootstrap_PromotesAnExistingMember()
    {
        var owner = await RegisterUserAsync(factory, ownerToken);

        await ExecuteAsync(
            factory,
            """
            INSERT INTO "Memberships" ("OrganizationId", "UserId", "Role", "CreatedUtc")
            SELECT 1, "Id", 'Member', 638000000000000000 FROM "Users" WHERE "PublicId" = {0}
            """,
            owner.ToString().ToUpperInvariant());

        Assert.Equal(LegacyOwnerBootstrapOutcome.OwnerAdded, await BootstrapAsync(owner.ToString()));
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "Memberships" WHERE "Role" = 'Owner'"""));
    }

    [Fact]
    public async Task Bootstrap_IgnoresUnknownAndDisabledUsers()
    {
        await RegisterUserAsync(factory, ownerToken);

        Assert.Equal(LegacyOwnerBootstrapOutcome.NotConfigured, await BootstrapAsync(""));
        Assert.Equal(LegacyOwnerBootstrapOutcome.UserNotFound, await BootstrapAsync(Guid.NewGuid().ToString()));

        var disabled = await RegisterUserAsync(factory, inviteeToken);
        await DisableAsync(disabled);

        Assert.Equal(LegacyOwnerBootstrapOutcome.UserDisabled, await BootstrapAsync(disabled.ToString()));
        Assert.Equal(0L, await CountAsync(factory, """SELECT COUNT(*) FROM "Memberships" """));
    }

    [Fact]
    public async Task Bootstrap_WithAValueThatIsNotAUserId_StopsTheApi()
    {
        await RegisterUserAsync(factory, ownerToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BootstrapAsync("owner@example.com"));
    }

    // Missing organisation access

    [Fact]
    public async Task RegisteredUserWithoutMembership_CannotInvite()
    {
        await RegisterUserAsync(factory, inviteeToken);

        using var response = await InviteAsync(inviteeToken, "someone@example.com", "Member");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("organization_not_found", await ProblemCodeAsync(response));
        Assert.Equal(0L, await CountAsync(factory, """SELECT COUNT(*) FROM "Invitations" """));
    }

    [Fact]
    public async Task UnregisteredUser_CannotInviteOrAccept()
    {
        using var invite = await InviteAsync(inviteeToken, "someone@example.com", "Member");
        using var accept = await AcceptAsync(inviteeToken, "anything");

        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
        Assert.Equal("user_not_registered", await ProblemCodeAsync(invite));
        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);
        Assert.Equal("user_not_registered", await ProblemCodeAsync(accept));
    }

    [Fact]
    public async Task InvitationEndpoints_RequireAValidToken()
    {
        using var invite = await InviteAsync(token: null, "someone@example.com", "Member");
        using var accept = await AcceptAsync("not-a-jwt", "anything");

        Assert.Equal(HttpStatusCode.Unauthorized, invite.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, accept.StatusCode);
    }

    [Fact]
    public async Task UnknownOrganisation_IsNotFound()
    {
        await CreateLegacyOwnerAsync();

        using var response =
            await SendAsync(
                factory,
                HttpMethod.Post,
                $"/api/organizations/{Guid.NewGuid()}/invitations",
                ownerToken,
                new { email = "someone@example.com", role = "Member" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Who may invite

    [Fact]
    public async Task Member_CannotInvite()
    {
        await CreateLegacyOwnerAsync();
        await JoinAsync(inviteeToken, "invitee@example.com", "Member");

        using var response = await InviteAsync(inviteeToken, "someone@example.com", "Member");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("organization_permission_denied", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Admin_MayInviteMembersButNotAdmins()
    {
        await CreateLegacyOwnerAsync();
        await JoinAsync(inviteeToken, "invitee@example.com", "Admin");

        using var member = await InviteAsync(inviteeToken, "member@example.com", "Member");
        using var admin = await InviteAsync(inviteeToken, "admin@example.com", "Admin");

        Assert.Equal(HttpStatusCode.Created, member.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("owner")]
    [InlineData("0")]
    [InlineData("Superuser")]
    [InlineData(null)]
    public async Task OwnershipAndUnknownRoles_CannotBeInvited(
        string? role)
    {
        await CreateLegacyOwnerAsync();

        using var response = await InviteAsync(ownerToken, "someone@example.com", role);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await CountAsync(factory, """SELECT COUNT(*) FROM "Invitations" """));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("@example.com")]
    [InlineData(null)]
    public async Task InvalidEmail_IsRejected(
        string? email)
    {
        await CreateLegacyOwnerAsync();

        using var response = await InviteAsync(ownerToken, email, "Member");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DisabledOwner_CannotInvite()
    {
        var owner = await CreateLegacyOwnerAsync();
        await DisableAsync(owner);

        using var response = await InviteAsync(ownerToken, "someone@example.com", "Member");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("user_disabled", await ProblemCodeAsync(response));
    }

    // Accepting

    [Fact]
    public async Task Invitee_WithTheTokenAndTheVerifiedEmail_BecomesAMember()
    {
        await CreateLegacyOwnerAsync();
        var token = await InvitationTokenAsync("Invitee@Example.com", "Member");
        await RegisterUserAsync(factory, inviteeToken);

        using var accepted = await AcceptAsync(inviteeToken, token);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await JsonAsync(accepted);
        Assert.Equal(LegacyOrganization.PublicId, body.GetProperty("publicId").GetGuid());
        Assert.Equal("Member", body.GetProperty("role").GetString());

        using var me = await GetMeAsync(factory, inviteeToken);
        var organization = Assert.Single((await JsonAsync(me)).GetProperty("user").GetProperty("organizations").EnumerateArray());
        Assert.Equal("Member", organization.GetProperty("role").GetString());

        Assert.Equal(
            1L,
            await CountAsync(factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'InvitationAccepted'"""));
    }

    [Fact]
    public async Task OnlyTheTokensHashIsStored_AndAuditDetailsHoldNoToken()
    {
        await CreateLegacyOwnerAsync();
        var token = await InvitationTokenAsync("invitee@example.com", "Member");

        Assert.Equal(0L, await CountAsync(factory, """SELECT COUNT(*) FROM "Invitations" WHERE "TokenHash" = @p0""", token));
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "Invitations" WHERE "TokenHash" = @p0""", InvitationToken.Hash(token)));
        Assert.Equal(
            0L,
            await CountAsync(
                factory,
                """SELECT COUNT(*) FROM "AuditEvents" WHERE instr(coalesce("Details", ''), @p0) > 0 OR instr(coalesce("Details", ''), @p1) > 0""",
                token,
                InvitationToken.Hash(token)));
    }

    [Fact]
    public async Task VerifiedEmailOfSomeoneElse_CannotAccept()
    {
        await CreateLegacyOwnerAsync();
        var token = await InvitationTokenAsync("invitee@example.com", "Member");

        var stranger = TokenFor("oid-stranger", "stranger@example.com");
        await RegisterUserAsync(factory, stranger);

        using var response = await AcceptAsync(stranger, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("invitation_email_mismatch", await ProblemCodeAsync(response));
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "Memberships" """));
    }

    [Fact]
    public async Task UnverifiedEmail_EvenTheInvitedAddress_CannotAccept()
    {
        await CreateLegacyOwnerAsync();
        var token = await InvitationTokenAsync("invitee@example.com", "Member");

        var unverified = TokenFor("oid-unverified", "invitee@example.com", emailVerified: false);
        await RegisterUserAsync(factory, unverified);

        using var response = await AcceptAsync(unverified, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("verified_email_required", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task InvitedEmailWithoutTheToken_GivesNoAccess()
    {
        await CreateLegacyOwnerAsync();
        await InvitationTokenAsync("invitee@example.com", "Member");
        await RegisterUserAsync(factory, inviteeToken);

        using var guessed = await AcceptAsync(inviteeToken, "invitee@example.com");
        using var missing = await AcceptAsync(inviteeToken, "");

        Assert.Equal(HttpStatusCode.NotFound, guessed.StatusCode);
        Assert.Equal("invitation_not_found", await ProblemCodeAsync(guessed));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "Memberships" """));
    }

    [Fact]
    public async Task AcceptingTwice_ReturnsTheSameMembership_ButNobodyElseCanUseTheToken()
    {
        await CreateLegacyOwnerAsync();
        var token = await InvitationTokenAsync("invitee@example.com", "Member");
        await RegisterUserAsync(factory, inviteeToken);

        using var first = await AcceptAsync(inviteeToken, token);
        using var second = await AcceptAsync(inviteeToken, token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Another identity with the same verified address (another tenant).
        var sameAddress = TokenFor("oid-invitee", "invitee@example.com", tenantId: EntraTestTokens.OtherTenantId);
        await RegisterUserAsync(factory, sameAddress);

        using var third = await AcceptAsync(sameAddress, token);

        Assert.Equal(HttpStatusCode.Gone, third.StatusCode);
        Assert.Equal("invitation_no_longer_valid", await ProblemCodeAsync(third));
        Assert.Equal(2L, await CountAsync(factory, """SELECT COUNT(*) FROM "Memberships" """));
    }

    [Fact]
    public async Task ExpiredInvitation_CannotBeAccepted()
    {
        await CreateLegacyOwnerAsync();
        var token = await InvitationTokenAsync("invitee@example.com", "Member");
        await RegisterUserAsync(factory, inviteeToken);

        baseFactory.Clock.UtcNow += InvitationService.Lifetime + TimeSpan.FromMinutes(1);

        using var response = await AcceptAsync(inviteeToken, token);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("invitation_expired", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task InvitingAgain_RevokesTheEarlierInvitation()
    {
        await CreateLegacyOwnerAsync();
        var old = await InvitationTokenAsync("invitee@example.com", "Member");
        var current = await InvitationTokenAsync("invitee@example.com", "Member");
        await RegisterUserAsync(factory, inviteeToken);

        using var withOld = await AcceptAsync(inviteeToken, old);
        using var withCurrent = await AcceptAsync(inviteeToken, current);

        Assert.Equal(HttpStatusCode.Gone, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withCurrent.StatusCode);
    }

    [Fact]
    public async Task InvitationOfAnInviterWhoLostTheRight_IsNoLongerValid()
    {
        await CreateLegacyOwnerAsync();
        await JoinAsync(TokenFor("oid-admin", "admin@example.com"), "admin@example.com", "Admin");

        var token =
            await InvitationTokenAsync("invitee@example.com", "Member", TokenFor("oid-admin", "admin@example.com"));

        await ExecuteAsync(
            factory,
            """
            UPDATE "Memberships" SET "Role" = 'Member'
            WHERE "UserId" = (SELECT "UserId" FROM "UserIdentities" WHERE "Subject" = 'oid-admin')
            """);

        await RegisterUserAsync(factory, inviteeToken);
        using var response = await AcceptAsync(inviteeToken, token);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("invitation_no_longer_valid", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DisabledInvitee_CannotAccept()
    {
        await CreateLegacyOwnerAsync();
        var token = await InvitationTokenAsync("invitee@example.com", "Member");
        var invitee = await RegisterUserAsync(factory, inviteeToken);
        await DisableAsync(invitee);

        using var response = await AcceptAsync(inviteeToken, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("user_disabled", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ExistingMember_GetsAConflict()
    {
        await CreateLegacyOwnerAsync();
        await JoinAsync(inviteeToken, "invitee@example.com", "Member");
        var token = await InvitationTokenAsync("invitee@example.com", "Admin");

        using var response = await AcceptAsync(inviteeToken, token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("already_member", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ParallelAccepts_CreateOneMembership()
    {
        await CreateLegacyOwnerAsync();
        var token = await InvitationTokenAsync("invitee@example.com", "Member");
        await RegisterUserAsync(factory, inviteeToken);

        var responses =
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AcceptAsync(inviteeToken, token)));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(2L, await CountAsync(factory, """SELECT COUNT(*) FROM "Memberships" """));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // Helpers

    private async Task<Guid> CreateLegacyOwnerAsync()
    {
        var owner = await RegisterUserAsync(factory, ownerToken);
        Assert.Equal(LegacyOwnerBootstrapOutcome.OwnerAdded, await BootstrapAsync(owner.ToString()));
        return owner;
    }

    /// <summary>Invited by the Owner, then registered and accepted.</summary>
    private async Task JoinAsync(
        string token,
        string email,
        string role)
    {
        var invitation = await InvitationTokenAsync(email, role);
        await RegisterUserAsync(factory, token);

        using var accepted = await AcceptAsync(token, invitation);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    private async Task<string> InvitationTokenAsync(
        string email,
        string role,
        string? inviter = null)
    {
        using var response = await InviteAsync(inviter ?? ownerToken, email, role);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await JsonAsync(response);
        Assert.Equal(email.Trim().ToLowerInvariant(), body.GetProperty("email").GetString());
        return body.GetProperty("token").GetString()!;
    }

    private Task<HttpResponseMessage> InviteAsync(
        string? token,
        string? email,
        string? role) =>
        SendAsync(factory, HttpMethod.Post, LegacyPath, token, new { email, role });

    private Task<HttpResponseMessage> AcceptAsync(
        string? token,
        string invitationToken) =>
        SendAsync(factory, HttpMethod.Post, "/api/invitations/accept", token, new { token = invitationToken });

    private async Task<LegacyOwnerBootstrapOutcome> BootstrapAsync(
        string configuredUserId)
    {
        using var scope = factory.Services.CreateScope();

        return await LegacyOwnerBootstrap.ApplyAsync(
            scope.ServiceProvider.GetRequiredService<BookingDbContext>(),
            configuredUserId,
            baseFactory.Clock,
            NullLogger.Instance);
    }

    private Task DisableAsync(
        Guid publicId) =>
        ExecuteAsync(
            factory,
            """UPDATE "Users" SET "DisabledUtc" = 638100000000000000 WHERE "PublicId" = {0}""",
            publicId.ToString().ToUpperInvariant());
}
