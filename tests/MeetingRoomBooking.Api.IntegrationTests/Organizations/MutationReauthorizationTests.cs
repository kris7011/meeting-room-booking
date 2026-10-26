using System.Net;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Organizations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;
using static MeetingRoomBooking.Api.IntegrationTests.Organizations.TenantScenario;

namespace MeetingRoomBooking.Api.IntegrationTests.Organizations;

/// <summary>
/// Changes whose caller loses their access while the request is running:
/// after the access filter has let it through, before its transaction
/// starts. The services check the caller again under the write lock
/// (<see cref="OrganizationAccessService.ReauthorizeAsync"/>), so such a
/// request gets the answer it would have got had it arrived after the
/// revocation, and writes nothing.
/// </summary>
/// <remarks>
/// <see cref="TransactionGate"/> holds the request at that exact point while
/// the test revokes the access, then lets it go on. No test depends on
/// timing. Each test has its own temporary database (<see cref="TenantScenario"/>).
/// </remarks>
public sealed class MutationReauthorizationTests
    : IAsyncLifetime
{
    public enum Revocation
    {
        /// <summary>An Owner makes them a Member (through the API).</summary>
        Demoted,

        /// <summary>An Owner removes them from the organisation (through the API).</summary>
        Removed,

        /// <summary>Their user is disabled (directly in the database; the API has no endpoint for it).</summary>
        Disabled
    }

    private const string RoomsSql =
        """
        SELECT group_concat("Id" || '|' || "Name" || '|' || "Capacity" || '|' || COALESCE("ArchivedUtc", ''), ';')
        FROM (SELECT * FROM "MeetingRooms" ORDER BY "Id")
        """;

    private const string BookingsSql =
        """
        SELECT group_concat("Id" || '|' || "RoomId" || '|' || "Title" || '|' || "StartUtc" || '|' || "EndUtc", ';')
        FROM (SELECT * FROM "Bookings" ORDER BY "Id")
        """;

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

    // Rooms: Owners and Admins

    public static TheoryData<string, Revocation> RoomChangesAndRevocations()
    {
        var data = new TheoryData<string, Revocation>();

        foreach (var change in new[] { "create", "update", "archive", "restore" })
        {
            foreach (var revocation in Enum.GetValues<Revocation>())
            {
                data.Add(change, revocation);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RoomChangesAndRevocations))]
    public async Task AdminLosingAccessMidRequest_ChangesNoRoom(
        string change,
        Revocation revocation)
    {
        if (change == "restore")
        {
            using var archived = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/archive", scenario.OwnerToken);
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        }

        var roomsBefore = await ScalarAsync(scenario.Factory, RoomsSql);
        var roomEventsBefore = await RoomEventsAsync();

        var (method, path, body) =
            change switch
            {
                "create" => (HttpMethod.Post, $"{LegacyPrefix}/rooms", new { name = "Race Room", capacity = 4 }),
                "update" => (HttpMethod.Put, $"{LegacyPrefix}/rooms/1", new { name = "Race Room", capacity = 4 }),
                "archive" => (HttpMethod.Post, $"{LegacyPrefix}/rooms/1/archive", null),
                _ => (HttpMethod.Post, $"{LegacyPrefix}/rooms/1/restore", (object?)null)
            };

        using var response =
            await SendWhileAsync(
                () => RevokeAsync(revocation, scenario.AdminId),
                method,
                path,
                scenario.AdminToken,
                body);

        await AssertRevokedAsync(response, revocation);
        Assert.Equal(roomsBefore, await ScalarAsync(scenario.Factory, RoomsSql));
        Assert.Equal(roomEventsBefore, await RoomEventsAsync());
    }

    [Fact]
    public async Task AnotherMembersRoleChangingMidRequest_DoesNotStopAnAuthorizedChange()
    {
        using var response =
            await SendWhileAsync(
                () => RevokeAsync(Revocation.Removed, scenario.MemberId),
                HttpMethod.Post,
                $"{LegacyPrefix}/rooms",
                scenario.AdminToken,
                new { name = "Race Room", capacity = 4 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'RoomCreated'"""));
    }

    // Bookings: every member, Members only their own

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task AdminDemotedMidRequest_CannotChangeOrDeleteSomeoneElsesBooking(
        string method)
    {
        var bookingId = await CreateBookingAsync(scenario.MemberToken);
        var bookingsBefore = await ScalarAsync(scenario.Factory, BookingsSql);

        using var response =
            await SendWhileAsync(
                () => RevokeAsync(Revocation.Demoted, scenario.AdminId),
                new HttpMethod(method),
                $"{LegacyPrefix}/bookings/{bookingId}",
                scenario.AdminToken,
                method == "PUT" ? scenario.BookingRequest(1, hours: 3, title: "Moved") : null);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "booking_permission_denied");
        Assert.Equal(bookingsBefore, await ScalarAsync(scenario.Factory, BookingsSql));
    }

    public static TheoryData<string, Revocation> BookingChangesAndRevocations()
    {
        var data = new TheoryData<string, Revocation>();

        foreach (var change in new[] { "create", "update", "delete" })
        {
            data.Add(change, Revocation.Removed);
            data.Add(change, Revocation.Disabled);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BookingChangesAndRevocations))]
    public async Task MemberLosingAccessMidRequest_ChangesNoBooking(
        string change,
        Revocation revocation)
    {
        var ownBookingId = await CreateBookingAsync(scenario.MemberToken);
        var bookingsBefore = await ScalarAsync(scenario.Factory, BookingsSql);

        var (method, path, body) =
            change switch
            {
                "create" => (HttpMethod.Post, $"{LegacyPrefix}/bookings", scenario.BookingRequest(1, hours: 3)),
                "update" => (HttpMethod.Put, $"{LegacyPrefix}/bookings/{ownBookingId}", scenario.BookingRequest(1, hours: 3, title: "Moved")),
                _ => (HttpMethod.Delete, $"{LegacyPrefix}/bookings/{ownBookingId}", null)
            };

        using var response =
            await SendWhileAsync(
                () => RevokeAsync(revocation, scenario.MemberId),
                method,
                path,
                scenario.MemberToken,
                body);

        await AssertRevokedAsync(response, revocation);
        Assert.Equal(bookingsBefore, await ScalarAsync(scenario.Factory, BookingsSql));
    }

    [Fact]
    public async Task OriginalBookingEndpoint_MemberRemovedMidRequest_CannotBook()
    {
        using var response =
            await SendWhileAsync(
                () => RevokeAsync(Revocation.Removed, scenario.MemberId),
                HttpMethod.Post,
                "/api/bookings",
                scenario.MemberToken,
                scenario.BookingRequest(1));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "organization_not_found");
        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" """));
    }

    [Fact]
    public async Task AdminDemotedMidRequest_CanStillChangeTheirOwnBooking()
    {
        var bookingId = await CreateBookingAsync(scenario.AdminToken);

        using var response =
            await SendWhileAsync(
                () => RevokeAsync(Revocation.Demoted, scenario.AdminId),
                HttpMethod.Put,
                $"{LegacyPrefix}/bookings/{bookingId}",
                scenario.AdminToken,
                scenario.BookingRequest(1, title: "Still mine"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" WHERE "Id" = @p0 AND "Title" = 'Still mine'""", bookingId));
    }

    [Fact]
    public async Task SlotBookedByAnotherMemberMidRequest_IsStillAnOverlap()
    {
        using var response =
            await SendWhileAsync(
                () => scenario.CreateBookingAsync(LegacyPrefix, scenario.OwnerToken, scenario.BookingRequest(1)),
                HttpMethod.Post,
                $"{LegacyPrefix}/bookings",
                scenario.MemberToken,
                scenario.BookingRequest(1));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "booking_overlap");
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" """));
    }

    // Across organisations

    [Fact]
    public async Task RemovalFromOneOrganizationMidRequest_DoesNotAffectAnotherOrganization()
    {
        await scenario.JoinAsync(scenario.OtherOrganizationId, scenario.OtherOwnerToken, scenario.AdminToken, "admin@example.com", "Member");

        using var response =
            await SendWhileAsync(
                () => RevokeAsync(Revocation.Removed, scenario.AdminId),
                HttpMethod.Post,
                $"{scenario.OtherPrefix}/bookings",
                scenario.AdminToken,
                scenario.BookingRequest(scenario.OtherRoomId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var legacy = await scenario.GetAsync($"{LegacyPrefix}/rooms", scenario.AdminToken);
        await AssertProblemAsync(legacy, HttpStatusCode.NotFound, "organization_not_found");
    }

    [Fact]
    public async Task MemberRemovedMidRequest_CannotTellTheOrganizationFromAMissingOne()
    {
        using var response =
            await SendWhileAsync(
                () => RevokeAsync(Revocation.Removed, scenario.MemberId),
                HttpMethod.Post,
                $"{LegacyPrefix}/bookings",
                scenario.MemberToken,
                scenario.BookingRequest(1));

        using var unknown =
            await scenario.SendAsync(
                HttpMethod.Post,
                $"/api/organizations/{Guid.NewGuid()}/bookings",
                scenario.MemberToken,
                scenario.BookingRequest(1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await ProblemWithoutTraceAsync(unknown), await ProblemWithoutTraceAsync(response));
    }

    // Organisation, invitations and members

    [Fact]
    public async Task OwnerDemotedMidRequest_CannotRenameTheOrganization()
    {
        var adminRole = $"{scenario.OtherPrefix}/members/{scenario.AdminId}/role";

        await scenario.JoinAsync(scenario.OtherOrganizationId, scenario.OtherOwnerToken, scenario.AdminToken, "admin@example.com", "Admin");

        using (var promoted = await scenario.SendAsync(HttpMethod.Put, adminRole, scenario.OtherOwnerToken, new { role = "Owner" }))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        }

        using var response =
            await SendWhileAsync(
                async () =>
                {
                    using var demoted = await scenario.SendAsync(HttpMethod.Put, adminRole, scenario.OtherOwnerToken, new { role = "Member" });
                    Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
                },
                HttpMethod.Put,
                scenario.OtherPrefix,
                scenario.AdminToken,
                new { name = "Taken Over" });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "organization_permission_denied");
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Organizations" WHERE "Name" = @p0""", OtherOrganizationName));
        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'OrganizationRenamed'"""));
    }

    [Fact]
    public async Task AdminDemotedMidRequest_CannotRevokeAnInvitation()
    {
        using (var invited =
                   await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/invitations", scenario.OwnerToken, new { email = "late@example.com", role = "Member" }))
        {
            Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        }

        var invitationId =
            await CountAsync(scenario.Factory, """SELECT "Id" FROM "Invitations" WHERE "Email" = 'late@example.com'""");

        using var response =
            await SendWhileAsync(
                () => RevokeAsync(Revocation.Demoted, scenario.AdminId),
                HttpMethod.Delete,
                $"{LegacyPrefix}/invitations/{invitationId}",
                scenario.AdminToken);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "organization_permission_denied");
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Invitations" WHERE "Id" = @p0 AND "RevokedUtc" IS NULL""", invitationId));
        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'InvitationRevoked'"""));
    }

    [Fact]
    public async Task OwnerDemotedByTheOtherOwnerMidRequest_CannotDemoteThemBack()
    {
        using (var promoted =
                   await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{scenario.AdminId}/role", scenario.OwnerToken, new { role = "Owner" }))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        }

        using var response =
            await SendWhileAsync(
                async () =>
                {
                    using var demoted =
                        await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{scenario.OwnerId}/role", scenario.AdminToken, new { role = "Member" });
                    Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
                },
                HttpMethod.Put,
                $"{LegacyPrefix}/members/{scenario.AdminId}/role",
                scenario.OwnerToken,
                new { role = "Member" });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "organization_permission_denied");
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Memberships" WHERE "OrganizationId" = 1 AND "Role" = 'Owner'"""));
        Assert.Equal(1L, await RoleCountAsync(scenario.AdminId, "Owner"));
        Assert.Equal(2L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'MembershipRoleChanged'"""));
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task OwnerDisabledMidRequest_CannotChangeOrRemoveMembers(
        string method)
    {
        using var response =
            await SendWhileAsync(
                () => RevokeAsync(Revocation.Disabled, scenario.OwnerId),
                new HttpMethod(method),
                method == "PUT"
                    ? $"{LegacyPrefix}/members/{scenario.MemberId}/role"
                    : $"{LegacyPrefix}/members/{scenario.MemberId}",
                scenario.OwnerToken,
                method == "PUT" ? new { role = "Admin" } : null);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "user_disabled");
        Assert.Equal(1L, await RoleCountAsync(scenario.MemberId, "Member"));
        Assert.Equal(
            0L,
            await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" IN ('MembershipRoleChanged', 'MembershipRemoved')"""));
    }

    // The check and the write share the write lock

    [Fact]
    public async Task ChangeHoldingTheWriteLock_IsWrittenBeforeARevocationCanBegin()
    {
        var (paused, request) =
            await HoldAsync(
                TransactionGatePoint.AfterBegin,
                HttpMethod.Post,
                $"{LegacyPrefix}/rooms",
                scenario.AdminToken,
                new { name = "Locked Room", capacity = 4 });

        using (paused)
        {
            // Any other writer, a demotion included, waits for the lock; with
            // a one-second busy timeout it gives up instead.
            await using var other =
                new SqliteConnection($"Data Source={scenario.DatabasePath};Pooling=False;Default Timeout=1");

            await other.OpenAsync();

            await using var begin = other.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE";

            var busy = await Assert.ThrowsAsync<SqliteException>(() => begin.ExecuteNonQueryAsync());
            Assert.Equal(5, busy.SqliteErrorCode); // SQLITE_BUSY
        }

        using (var response = await request)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await RevokeAsync(Revocation.Demoted, scenario.AdminId);

        using var next =
            await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.AdminToken, new { name = "Later Room", capacity = 4 });

        await AssertProblemAsync(next, HttpStatusCode.Forbidden, "organization_permission_denied");
    }

    [Fact]
    public async Task Reauthorizing_OutsideAWriteTransaction_IsRefused()
    {
        using var scope = scenario.Factory.Services.CreateScope();

        var access = scope.ServiceProvider.GetRequiredService<OrganizationAccessService>();

        var caller =
            new OrganizationCaller(
                UserId: 1,
                UserPublicId: scenario.OwnerId,
                DisplayName: "Olivia Owner",
                OrganizationId: LegacyOrganization.Id,
                OrganizationPublicId: LegacyOrganization.PublicId,
                OrganizationName: "Legacy",
                IsLegacy: true,
                Role: MembershipRole.Owner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => access.ReauthorizeAsync(caller, OrganizationRoleRequirement.AnyMember, CancellationToken.None));
    }

    // Helpers

    /// <summary>
    /// Sends a request, holds it after the access filter and before its
    /// transaction, runs <paramref name="meanwhile"/>, then lets it finish.
    /// </summary>
    private async Task<HttpResponseMessage> SendWhileAsync(
        Func<Task> meanwhile,
        HttpMethod method,
        string path,
        string token,
        object? body = null)
    {
        var (paused, request) =
            await HoldAsync(TransactionGatePoint.BeforeBegin, method, path, token, body);

        using (paused)
        {
            await meanwhile();
        }

        return await request;
    }

    private async Task<(PausedTransaction Paused, Task<HttpResponseMessage> Request)> HoldAsync(
        TransactionGatePoint point,
        HttpMethod method,
        string path,
        string token,
        object? body)
    {
        var paused = scenario.Transactions.PauseNext(point);
        var request = scenario.SendAsync(method, path, token, body);

        if (await Task.WhenAny(paused.Reached, request) != paused.Reached)
        {
            paused.Dispose();

            using var early = await request;
            Assert.Fail($"The request ended ({(int)early.StatusCode}) before it started a transaction.");
        }

        return (paused, request);
    }

    private async Task RevokeAsync(
        Revocation revocation,
        Guid userId)
    {
        switch (revocation)
        {
            case Revocation.Demoted:
                using (var demoted =
                           await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{userId}/role", scenario.OwnerToken, new { role = "Member" }))
                {
                    Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
                }

                break;

            case Revocation.Removed:
                using (var removed = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/members/{userId}", scenario.OwnerToken))
                {
                    Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
                }

                break;

            case Revocation.Disabled:
                await ExecuteAsync(
                    scenario.Factory,
                    """UPDATE "Users" SET "DisabledUtc" = 638100000000000000 WHERE "PublicId" = {0}""",
                    userId.ToString().ToUpperInvariant());

                break;
        }
    }

    /// <summary>The answer a request sent after the revocation gets from the access filter.</summary>
    private static Task AssertRevokedAsync(
        HttpResponseMessage response,
        Revocation revocation) =>
        revocation switch
        {
            Revocation.Demoted => AssertProblemAsync(response, HttpStatusCode.Forbidden, "organization_permission_denied"),
            Revocation.Removed => AssertProblemAsync(response, HttpStatusCode.NotFound, "organization_not_found"),
            _ => AssertProblemAsync(response, HttpStatusCode.Forbidden, "user_disabled")
        };

    private async Task<int> CreateBookingAsync(
        string token) =>
        (await scenario.CreateBookingAsync(LegacyPrefix, token, scenario.BookingRequest(1)))
        .GetProperty("id")
        .GetInt32();

    private Task<long> RoomEventsAsync() =>
        CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" LIKE 'Room%'""");

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
