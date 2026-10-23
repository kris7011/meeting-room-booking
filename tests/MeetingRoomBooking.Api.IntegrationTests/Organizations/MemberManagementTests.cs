using System.Net;
using System.Text.Json;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;
using static MeetingRoomBooking.Api.IntegrationTests.Organizations.TenantScenario;

namespace MeetingRoomBooking.Api.IntegrationTests.Organizations;

/// <summary>
/// Members, roles and invitations of an organisation: who may see, change
/// and remove whom, and that an organisation never loses its last Owner.
/// </summary>
public sealed class MemberManagementTests
    : IAsyncLifetime
{
    private TenantScenario scenario = null!;

    public async Task InitializeAsync()
    {
        scenario = await TenantScenario.CreateAsync();
    }

    public Task DisposeAsync()
    {
        scenario.Dispose();
        return Task.CompletedTask;
    }

    // Member list

    [Fact]
    public async Task OwnersAndAdmins_SeeTheMembers_MembersDoNot()
    {
        using (var owner = await scenario.GetAsync($"{LegacyPrefix}/members", scenario.OwnerToken))
        {
            var members = (await JsonAsync(owner)).EnumerateArray().ToArray();

            Assert.Equal(["Owner", "Admin", "Member"], members.Select(m => m.GetProperty("role").GetString()!));
            Assert.Equal(scenario.OwnerId, members[0].GetProperty("userPublicId").GetGuid());
            Assert.True(members[0].GetProperty("isCurrentUser").GetBoolean());
            Assert.False(members[1].GetProperty("isCurrentUser").GetBoolean());
            Assert.Equal("Mia Member", members[2].GetProperty("displayName").GetString());
        }

        using (var admin = await scenario.GetAsync($"{LegacyPrefix}/members", scenario.AdminToken))
        {
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        }

        using var member = await scenario.GetAsync($"{LegacyPrefix}/members", scenario.MemberToken);
        await AssertProblemAsync(member, HttpStatusCode.Forbidden, "organization_permission_denied");
    }

    // Roles

    [Fact]
    public async Task Owner_ChangesRoles_AndTheChangeIsAudited()
    {
        using var promoted = await ChangeRoleAsync(scenario.OwnerToken, scenario.MemberId, "admin");

        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal("Admin", (await JsonAsync(promoted)).GetProperty("role").GetString());
        Assert.Equal("Admin", await RoleOfAsync(scenario.MemberId));

        // The new Admin may now manage rooms.
        using var room =
            await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.MemberToken, new { name = "Promoted Room", capacity = 2 });
        Assert.Equal(HttpStatusCode.Created, room.StatusCode);

        Assert.Equal(
            1L,
            await CountAsync(
                scenario.Factory,
                """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'MembershipRoleChanged' AND "Details" LIKE '%"oldRole":"Member","newRole":"Admin"%'"""));
    }

    [Fact]
    public async Task Admin_CannotMakeThemselvesOrAnyoneOwner()
    {
        using (var self = await ChangeRoleAsync(scenario.AdminToken, scenario.AdminId, "Owner"))
        {
            await AssertProblemAsync(self, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var other = await ChangeRoleAsync(scenario.AdminToken, scenario.MemberId, "Owner"))
        {
            await AssertProblemAsync(other, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var promote = await ChangeRoleAsync(scenario.AdminToken, scenario.MemberId, "Admin"))
        {
            await AssertProblemAsync(promote, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var member = await ChangeRoleAsync(scenario.MemberToken, scenario.MemberId, "Owner"))
        {
            await AssertProblemAsync(member, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        Assert.Equal("Admin", await RoleOfAsync(scenario.AdminId));
        Assert.Equal("Member", await RoleOfAsync(scenario.MemberId));
        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'MembershipRoleChanged'"""));
    }

    [Theory]
    [InlineData("Superuser")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public async Task UnknownRoles_AreRejected(
        string? role)
    {
        using var response = await ChangeRoleAsync(scenario.OwnerToken, scenario.MemberId, role);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Member", await RoleOfAsync(scenario.MemberId));
    }

    [Fact]
    public async Task OwnershipIsHandedOver_ByPromotingSomeoneElseFirst()
    {
        // Alone, the Owner can neither step down nor leave.
        using (var demote = await ChangeRoleAsync(scenario.OwnerToken, scenario.OwnerId, "Admin"))
        {
            await AssertProblemAsync(demote, HttpStatusCode.Conflict, "last_owner");
        }

        using (var leave = await RemoveAsync(scenario.OwnerToken, scenario.OwnerId))
        {
            await AssertProblemAsync(leave, HttpStatusCode.Conflict, "last_owner");
        }

        using (var promote = await ChangeRoleAsync(scenario.OwnerToken, scenario.AdminId, "Owner"))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        }

        using (var stepDown = await ChangeRoleAsync(scenario.OwnerToken, scenario.OwnerId, "Member"))
        {
            Assert.Equal(HttpStatusCode.OK, stepDown.StatusCode);
        }

        Assert.Equal("Owner", await RoleOfAsync(scenario.AdminId));
        Assert.Equal("Member", await RoleOfAsync(scenario.OwnerId));

        // The new sole Owner is protected in turn.
        using var removeLast = await RemoveAsync(scenario.AdminToken, scenario.AdminId);
        await AssertProblemAsync(removeLast, HttpStatusCode.Conflict, "last_owner");
    }

    [Fact]
    public async Task TwoOwnersRemovingEachOtherAtOnce_LeaveOneOwner()
    {
        using (var promote = await ChangeRoleAsync(scenario.OwnerToken, scenario.AdminId, "Owner"))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        }

        var responses =
            await Task.WhenAll(
                RemoveAsync(scenario.OwnerToken, scenario.AdminId),
                RemoveAsync(scenario.AdminToken, scenario.OwnerId));

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Memberships" WHERE "OrganizationId" = 1 AND "Role" = 'Owner'"""));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // Removing members

    [Fact]
    public async Task Owner_RemovesAMember_WhoLosesAccessButKeepsTheirBookings()
    {
        var booking = await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1));

        using (var removed = await RemoveAsync(scenario.OwnerToken, scenario.MemberId))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using (var rooms = await scenario.GetAsync($"{LegacyPrefix}/rooms", scenario.MemberToken))
        {
            await AssertProblemAsync(rooms, HttpStatusCode.NotFound, "organization_not_found");
        }

        using (var legacy = await scenario.GetAsync("/api/bookings", scenario.MemberToken))
        {
            await AssertProblemAsync(legacy, HttpStatusCode.NotFound, "organization_not_found");
        }

        using (var kept = await scenario.GetAsync($"{LegacyPrefix}/bookings/{booking.GetProperty("id").GetInt32()}", scenario.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        }

        Assert.Equal(
            1L,
            await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'MembershipRemoved' AND "Details" LIKE '%"reason":"removed"%'"""));
    }

    [Fact]
    public async Task AdminsAndMembers_CannotRemoveOthers_ButAnyoneCanLeave()
    {
        using (var admin = await RemoveAsync(scenario.AdminToken, scenario.MemberId))
        {
            await AssertProblemAsync(admin, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var member = await RemoveAsync(scenario.MemberToken, scenario.AdminId))
        {
            await AssertProblemAsync(member, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var adminOwner = await RemoveAsync(scenario.AdminToken, scenario.OwnerId))
        {
            await AssertProblemAsync(adminOwner, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var leave = await RemoveAsync(scenario.MemberToken, scenario.MemberId))
        {
            Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        }

        Assert.Equal(2L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Memberships" WHERE "OrganizationId" = 1"""));
        Assert.Equal(
            1L,
            await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'MembershipRemoved' AND "Details" LIKE '%"reason":"left"%'"""));
    }

    [Fact]
    public async Task UsersOutsideTheOrganization_AreNotMembersToChangeOrRemove()
    {
        var outsiderId = await RegisterUserAsync(scenario.Factory, scenario.OutsiderToken);

        foreach (var id in new[] { outsiderId, Guid.NewGuid() })
        {
            using var role = await ChangeRoleAsync(scenario.OwnerToken, id, "Admin");
            await AssertProblemAsync(role, HttpStatusCode.NotFound, "member_not_found");

            using var remove = await RemoveAsync(scenario.OwnerToken, id);
            await AssertProblemAsync(remove, HttpStatusCode.NotFound, "member_not_found");
        }

        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Memberships" AS m JOIN "Users" AS u ON u."Id" = m."UserId" WHERE u."PublicId" = @p0""", outsiderId.ToString().ToUpperInvariant()));
    }

    // Invitations

    [Fact]
    public async Task OpenInvitations_AreListedWithoutTheirTokens()
    {
        var token = await InviteAsync(scenario.OwnerToken, "new.person@example.com", "Admin");
        await InviteAsync(scenario.AdminToken, "another@example.com", "Member");

        using var list = await scenario.GetAsync($"{LegacyPrefix}/invitations", scenario.AdminToken);
        var json = await list.Content.ReadAsStringAsync();

        Assert.DoesNotContain(token, json);
        Assert.DoesNotContain(InvitationToken.Hash(token), json);
        Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);

        var invitations = JsonDocument.Parse(json).RootElement.EnumerateArray().ToArray();

        // Accepted invitations (those of the scenario's Admin and Member) are not open.
        Assert.Equal(2, invitations.Length);
        var adminInvitation = invitations.Single(i => i.GetProperty("email").GetString() == "new.person@example.com");
        Assert.Equal("Admin", adminInvitation.GetProperty("role").GetString());
        Assert.Equal("Olivia Owner", adminInvitation.GetProperty("invitedBy").GetString());
        Assert.False(adminInvitation.GetProperty("isExpired").GetBoolean());
        // An Admin may not revoke an Admin invitation; the list says so.
        Assert.False(adminInvitation.GetProperty("canRevoke").GetBoolean());
        Assert.True(invitations.Single(i => i.GetProperty("email").GetString() == "another@example.com").GetProperty("canRevoke").GetBoolean());

        using var member = await scenario.GetAsync($"{LegacyPrefix}/invitations", scenario.MemberToken);
        await AssertProblemAsync(member, HttpStatusCode.Forbidden, "organization_permission_denied");
    }

    [Fact]
    public async Task RevokedInvitation_CanNoLongerBeAccepted()
    {
        var token = await InviteAsync(scenario.OwnerToken, "outsider@example.com", "Member");
        var id = await OpenInvitationIdAsync("outsider@example.com");

        using (var revoked = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/invitations/{id}", scenario.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        using (var again = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/invitations/{id}", scenario.OwnerToken))
        {
            await AssertProblemAsync(again, HttpStatusCode.NotFound, "invitation_not_found");
        }

        await RegisterUserAsync(scenario.Factory, scenario.OutsiderToken);

        using (var accept = await AcceptAsync(scenario.OutsiderToken, token))
        {
            await AssertProblemAsync(accept, HttpStatusCode.Gone, "invitation_no_longer_valid");
        }

        Assert.Equal(
            1L,
            await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'InvitationRevoked' AND "Details" LIKE '%"reason":"revoked"%'"""));
    }

    [Fact]
    public async Task Admins_RevokeOnlyMemberInvitations()
    {
        await InviteAsync(scenario.OwnerToken, "future.admin@example.com", "Admin");
        await InviteAsync(scenario.OwnerToken, "future.member@example.com", "Member");

        using (var adminInvitation =
                   await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/invitations/{await OpenInvitationIdAsync("future.admin@example.com")}", scenario.AdminToken))
        {
            await AssertProblemAsync(adminInvitation, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var memberInvitation =
                   await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/invitations/{await OpenInvitationIdAsync("future.member@example.com")}", scenario.AdminToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, memberInvitation.StatusCode);
        }

        using (var member =
                   await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/invitations/{await OpenInvitationIdAsync("future.admin@example.com")}", scenario.MemberToken))
        {
            await AssertProblemAsync(member, HttpStatusCode.Forbidden, "organization_permission_denied");
        }
    }

    [Fact]
    public async Task InvitationsOfAnotherOrganization_CannotBeRevoked()
    {
        await InviteAsync(scenario.OwnerToken, "someone@example.com", "Member");
        var id = await OpenInvitationIdAsync("someone@example.com");

        using var response = await scenario.SendAsync(HttpMethod.Delete, $"{scenario.OtherPrefix}/invitations/{id}", scenario.OtherOwnerToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "invitation_not_found");
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Invitations" WHERE "Id" = @p0 AND "RevokedUtc" IS NULL""", id));
    }

    [Fact]
    public async Task Invitation_IsCreatedAcceptedAndThenNoLongerOpen()
    {
        var token = await InviteAsync(scenario.AdminToken, "outsider@example.com", "Member");
        await RegisterUserAsync(scenario.Factory, scenario.OutsiderToken);

        using (var accepted = await AcceptAsync(scenario.OutsiderToken, token))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal("Member", (await JsonAsync(accepted)).GetProperty("role").GetString());
        }

        using (var rooms = await scenario.GetAsync($"{LegacyPrefix}/rooms", scenario.OutsiderToken))
        {
            Assert.Equal(HttpStatusCode.OK, rooms.StatusCode);
        }

        using var list = await scenario.GetAsync($"{LegacyPrefix}/invitations", scenario.OwnerToken);
        Assert.Empty((await JsonAsync(list)).EnumerateArray());
    }

    [Fact]
    public async Task ExpiredInvitation_IsListedAsExpired_AndRefused()
    {
        var token = await InviteAsync(scenario.OwnerToken, "outsider@example.com", "Member");
        await RegisterUserAsync(scenario.Factory, scenario.OutsiderToken);

        scenario.Clock.UtcNow = scenario.Clock.UtcNow + InvitationService.Lifetime + TimeSpan.FromMinutes(1);

        using (var list = await scenario.GetAsync($"{LegacyPrefix}/invitations", scenario.OwnerToken))
        {
            Assert.True(Assert.Single((await JsonAsync(list)).EnumerateArray()).GetProperty("isExpired").GetBoolean());
        }

        using var accept = await AcceptAsync(scenario.OutsiderToken, token);
        await AssertProblemAsync(accept, HttpStatusCode.Gone, "invitation_expired");
    }

    [Fact]
    public async Task InvitationOfARemovedAdmin_StopsWorking()
    {
        var token = await InviteAsync(scenario.AdminToken, "outsider@example.com", "Member");

        using (var removed = await RemoveAsync(scenario.OwnerToken, scenario.AdminId))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        await RegisterUserAsync(scenario.Factory, scenario.OutsiderToken);

        using var accept = await AcceptAsync(scenario.OutsiderToken, token);
        await AssertProblemAsync(accept, HttpStatusCode.Gone, "invitation_no_longer_valid");
    }

    [Fact]
    public async Task Invitation_ForAnotherAccount_IsRefused_EvenWithTheToken()
    {
        var token = await InviteAsync(scenario.OwnerToken, "someone.else@example.com", "Member");
        await RegisterUserAsync(scenario.Factory, scenario.OutsiderToken);

        using var accept = await AcceptAsync(scenario.OutsiderToken, token);

        await AssertProblemAsync(accept, HttpStatusCode.Forbidden, "invitation_email_mismatch");
    }

    // Helpers

    private Task<HttpResponseMessage> ChangeRoleAsync(
        string token,
        Guid userId,
        string? role) =>
        scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{userId}/role", token, new { role });

    private Task<HttpResponseMessage> RemoveAsync(
        string token,
        Guid userId) =>
        scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/members/{userId}", token);

    private Task<HttpResponseMessage> AcceptAsync(
        string token,
        string invitationToken) =>
        scenario.SendAsync(HttpMethod.Post, "/api/invitations/accept", token, new { token = invitationToken });

    private async Task<string> InviteAsync(
        string inviterToken,
        string email,
        string role)
    {
        using var response =
            await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/invitations", inviterToken, new { email, role });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await JsonAsync(response)).GetProperty("token").GetString()!;
    }

    private async Task<int> OpenInvitationIdAsync(
        string email) =>
        (int)await CountAsync(
            scenario.Factory,
            """SELECT "Id" FROM "Invitations" WHERE "Email" = @p0 AND "AcceptedUtc" IS NULL AND "RevokedUtc" IS NULL""",
            email);

    // Read from the database, not the API: the caller may have lost the
    // right to list members by then.
    private async Task<string?> RoleOfAsync(
        Guid userId)
    {
        using var scope = scenario.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookingDbContext>();

        var role =
            await dbContext.Memberships
                .Where(membership => membership.OrganizationId == LegacyOrganization.Id)
                .Join(dbContext.Users.Where(user => user.PublicId == userId), membership => membership.UserId, user => user.Id, (membership, _) => membership.Role)
                .Select(candidate => (MembershipRole?)candidate)
                .SingleOrDefaultAsync();

        return role?.ToString();
    }
}
