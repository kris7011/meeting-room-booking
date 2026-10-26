using System.Net;
using System.Net.Http.Json;
using MeetingRoomBooking.Api.Features.Organizations;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;
using static MeetingRoomBooking.Api.IntegrationTests.Organizations.TenantScenario;

namespace MeetingRoomBooking.Api.IntegrationTests.Organizations;

/// <summary>
/// The role/endpoint matrix of docs/role-permission-matrix.md, and the
/// cases the other organisation tests leave open: roles read from the
/// database on every request, concurrent role changes, disabled users on the
/// organisation list, and non-members unable to tell an existing
/// organisation from a missing one.
/// </summary>
public sealed class RolePermissionMatrixTests
    : IAsyncLifetime
{
    private const string Prefix = OrganizationAccessExtensions.OrganizationRoutePrefix;

    private static readonly MembershipRole[] OwnerOnly = [MembershipRole.Owner];

    private static readonly MembershipRole[] OwnerOrAdmin = [MembershipRole.Owner, MembershipRole.Admin];

    /// <summary>
    /// Every endpoint under /api/organizations/{organizationId} and the roles
    /// its <see cref="OrganizationRoleRequirement"/> admits; null means every
    /// member. Finer rules (Admins invite and revoke only Members, Members
    /// change only their own bookings, only Owners remove others) are in the
    /// services and have their own tests.
    /// </summary>
    private static readonly Dictionary<string, MembershipRole[]?> ExpectedRoleRequirements =
        new()
        {
            [$"GET {Prefix}"] = null,
            [$"PUT {Prefix}"] = OwnerOnly,
            [$"GET {Prefix}/rooms"] = null,
            [$"GET {Prefix}/rooms/available"] = null,
            [$"POST {Prefix}/rooms"] = OwnerOrAdmin,
            [$"PUT {Prefix}/rooms/{{roomId:int}}"] = OwnerOrAdmin,
            [$"POST {Prefix}/rooms/{{roomId:int}}/archive"] = OwnerOrAdmin,
            [$"POST {Prefix}/rooms/{{roomId:int}}/restore"] = OwnerOrAdmin,
            [$"GET {Prefix}/bookings"] = null,
            [$"GET {Prefix}/bookings/{{bookingId:int}}"] = null,
            [$"POST {Prefix}/bookings"] = null,
            [$"PUT {Prefix}/bookings/{{bookingId:int}}"] = null,
            [$"DELETE {Prefix}/bookings/{{bookingId:int}}"] = null,
            [$"GET {Prefix}/members"] = OwnerOrAdmin,
            [$"PUT {Prefix}/members/{{userId:guid}}/role"] = OwnerOnly,
            [$"DELETE {Prefix}/members/{{userId:guid}}"] = null,
            [$"GET {Prefix}/invitations"] = OwnerOrAdmin,
            [$"DELETE {Prefix}/invitations/{{invitationId:int}}"] = OwnerOrAdmin,

            // Mapped outside the organisation group: InvitationService checks
            // membership and role itself (Owners invite Admins and Members,
            // Admins only Members).
            [$"POST {Prefix}/invitations"] = null
        };

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

    // The declared matrix

    [Fact]
    public void EveryOrganizationEndpoint_RequiresExactlyTheRolesOfTheMatrix()
    {
        var actual =
            scenario.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(Prefix, StringComparison.Ordinal) == true)
                .SelectMany(
                    endpoint =>
                        (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                        .Select(
                            method =>
                                // A group's "" route ends in "/"; clients call it without.
                                (Key: $"{method} {endpoint.RoutePattern.RawText!.TrimEnd('/')}",
                                 Roles: endpoint.Metadata.GetMetadata<OrganizationRoleRequirement>()?.Roles.Order().ToArray())))
                .ToDictionary(entry => entry.Key, entry => entry.Roles);

        // A new endpoint must be added to the matrix (and its documentation)
        // with a deliberate decision about who may call it.
        var unexpected = actual.Keys.Except(ExpectedRoleRequirements.Keys).ToArray();
        var missing = ExpectedRoleRequirements.Keys.Except(actual.Keys).ToArray();

        Assert.True(
            unexpected.Length == 0 && missing.Length == 0,
            $"Not in the matrix: [{string.Join(", ", unexpected)}]. Not mapped: [{string.Join(", ", missing)}].");

        foreach (var (endpoint, roles) in ExpectedRoleRequirements)
        {
            Assert.True(
                roles is null ? actual[endpoint] is null : actual[endpoint]?.SequenceEqual(roles.Order()) == true,
                $"{endpoint} requires [{string.Join(", ", actual[endpoint] ?? [])}], the matrix says [{string.Join(", ", roles ?? [])}].");
        }
    }

    // Member

    [Fact]
    public async Task Member_CannotRestoreRoomsRenameOrChangeAnotherMembersRole()
    {
        using (var archived = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/archive", scenario.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        }

        using (var restore = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/restore", scenario.MemberToken))
        {
            await AssertProblemAsync(restore, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var rename = await scenario.SendAsync(HttpMethod.Put, LegacyPrefix, scenario.MemberToken, new { name = "Taken Over" }))
        {
            await AssertProblemAsync(rename, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var demoteAdmin =
                   await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{scenario.AdminId}/role", scenario.MemberToken, new { role = "Member" }))
        {
            await AssertProblemAsync(demoteAdmin, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "MeetingRooms" WHERE "Id" = 1 AND "ArchivedUtc" IS NOT NULL"""));
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Organizations" WHERE "Id" = 1 AND "Name" = 'Legacy'"""));
        Assert.Equal(1L, await RoleCountAsync(scenario.AdminId, "Admin"));
        Assert.Equal(
            0L,
            await CountAsync(
                scenario.Factory,
                """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" IN ('RoomRestored', 'OrganizationRenamed', 'MembershipRoleChanged')"""));
    }

    // Roles come from the database on every request, not from the token

    [Fact]
    public async Task DemotedAdmin_LosesTheAdminRightsOnTheNextRequest_WithTheSameToken()
    {
        var memberBookingId =
            (await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1)))
            .GetProperty("id").GetInt32();

        using (var before = await scenario.GetAsync($"{LegacyPrefix}/members", scenario.AdminToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using (var demoted =
                   await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{scenario.AdminId}/role", scenario.OwnerToken, new { role = "Member" }))
        {
            Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        }

        using (var members = await scenario.GetAsync($"{LegacyPrefix}/members", scenario.AdminToken))
        {
            await AssertProblemAsync(members, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var room = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.AdminToken, new { name = "Late Room", capacity = 2 }))
        {
            await AssertProblemAsync(room, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var invite =
                   await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/invitations", scenario.AdminToken, new { email = "late@example.com", role = "Member" }))
        {
            await AssertProblemAsync(invite, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var othersBooking = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/bookings/{memberBookingId}", scenario.AdminToken))
        {
            await AssertProblemAsync(othersBooking, HttpStatusCode.Forbidden, "booking_permission_denied");
        }

        // Still a member: booking itself works.
        using (var rooms = await scenario.GetAsync($"{LegacyPrefix}/rooms", scenario.AdminToken))
        {
            Assert.Equal(HttpStatusCode.OK, rooms.StatusCode);
        }

        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "MeetingRooms" WHERE "Name" = 'Late Room'"""));
        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Invitations" WHERE "Email" = 'late@example.com'"""));
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" WHERE "Id" = @p0""", memberBookingId));
    }

    // Concurrent role changes

    [Fact]
    public async Task TwoOwnersDemotingEachOtherAtOnce_KeepExactlyOneOwner()
    {
        using (var promote =
                   await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{scenario.AdminId}/role", scenario.OwnerToken, new { role = "Owner" }))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        }

        var responses =
            await Task.WhenAll(
                scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{scenario.AdminId}/role", scenario.OwnerToken, new { role = "Member" }),
                scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{scenario.OwnerId}/role", scenario.AdminToken, new { role = "Member" }));

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));

            // The loser ran after the winner had demoted it, so it is no
            // longer an Owner (or, as a backstop, the last-Owner rule fires).
            var loser = responses.Single(response => response.StatusCode != HttpStatusCode.OK);
            Assert.Contains(await ProblemCodeAsync(loser), new[] { "organization_permission_denied", "last_owner" });

            Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Memberships" WHERE "OrganizationId" = 1 AND "Role" = 'Owner'"""));
            Assert.Equal(2L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'MembershipRoleChanged'"""));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // Users without access

    [Fact]
    public async Task DisabledUser_CannotListOrCreateOrganizations()
    {
        await ExecuteAsync(
            scenario.Factory,
            """UPDATE "Users" SET "DisabledUtc" = 638100000000000000 WHERE "PublicId" = {0}""",
            scenario.OwnerId.ToString().ToUpperInvariant());

        using (var list = await scenario.GetAsync("/api/organizations", scenario.OwnerToken))
        {
            await AssertProblemAsync(list, HttpStatusCode.Forbidden, "user_disabled");
        }

        using var client = scenario.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/organizations");
        request.Headers.Authorization = new("Bearer", scenario.OwnerToken);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { name = "Disabled Org" });

        using var created = await client.SendAsync(request);

        await AssertProblemAsync(created, HttpStatusCode.Forbidden, "user_disabled");
        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Organizations" WHERE "Name" = 'Disabled Org'"""));
    }

    [Fact]
    public async Task NonMembers_CannotTellAnExistingOrganizationFromAMissingOne()
    {
        // The organisation list holds the caller's memberships and nothing else.
        using (var list = await scenario.GetAsync("/api/organizations", scenario.OutsiderToken))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Empty((await JsonAsync(list)).EnumerateArray());
        }

        var missing = $"/api/organizations/{Guid.NewGuid()}";

        // Through the organisation group's access filter, and through the
        // invitation service, which checks membership itself.
        (HttpMethod Method, string Path, object? Body)[] requests =
        [
            (HttpMethod.Get, "", null),
            (HttpMethod.Get, "/members", null),
            (HttpMethod.Post, "/invitations", new { email = "probe@example.com", role = "Member" })
        ];

        foreach (var (method, path, body) in requests)
        {
            using var existing = await scenario.SendAsync(method, scenario.OtherPrefix + path, scenario.OutsiderToken, body);
            using var unknown = await scenario.SendAsync(method, missing + path, scenario.OutsiderToken, body);

            Assert.Equal(HttpStatusCode.NotFound, existing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            var existingProblem = await ProblemWithoutTraceAsync(existing);

            Assert.Contains("\"code\":\"organization_not_found\"", existingProblem);
            Assert.Equal(await ProblemWithoutTraceAsync(unknown), existingProblem);
        }

        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Invitations" WHERE "Email" = 'probe@example.com'"""));
    }

    // Helpers

    private Task<long> RoleCountAsync(
        Guid userId,
        string role) =>
        CountAsync(
            scenario.Factory,
            """
            SELECT COUNT(*) FROM "Memberships" AS m JOIN "Users" AS u ON u."Id" = m."UserId"
            WHERE m."OrganizationId" = 1 AND u."PublicId" = @p0 AND m."Role" = @p1
            """,
            userId.ToString().ToUpperInvariant(),
            role);
}
