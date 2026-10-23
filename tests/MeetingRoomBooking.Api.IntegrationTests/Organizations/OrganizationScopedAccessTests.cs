using System.Net;
using System.Text.Json;
using MeetingRoomBooking.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;
using static MeetingRoomBooking.Api.IntegrationTests.Organizations.TenantScenario;

namespace MeetingRoomBooking.Api.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenant access control: who may read and change an organisation's
/// rooms and bookings, through /api/organizations/{id}/… and through the
/// original endpoints, which now belong to the legacy organisation's members.
/// </summary>
public sealed class OrganizationScopedAccessTests
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

    // Every organisation endpoint, found from the API's own route table, so
    // an endpoint added later is covered without changing this test.

    [Fact]
    public async Task EveryOrganizationEndpoint_RejectsAnonymousCallersAndNonMembers()
    {
        var endpoints =
            OrganizationEndpoints(scenario.OtherOrganizationId);

        Assert.NotEmpty(endpoints);

        foreach (var (method, path) in endpoints)
        {
            using var anonymous =
                await scenario.SendAsync(method, path, token: null, BodyFor(method));

            Assert.True(
                anonymous.StatusCode == HttpStatusCode.Unauthorized,
                $"{method} {path} without a token answered {(int)anonymous.StatusCode}.");

            using var invalid =
                await scenario.SendAsync(method, path, "not-a-jwt", BodyFor(method));

            Assert.True(
                invalid.StatusCode == HttpStatusCode.Unauthorized,
                $"{method} {path} with an invalid token answered {(int)invalid.StatusCode}.");

            // Members of Legacy, of every role, are strangers to the other organisation.
            foreach (var token in new[] { scenario.OwnerToken, scenario.AdminToken, scenario.MemberToken, scenario.OutsiderToken })
            {
                using var stranger =
                    await scenario.SendAsync(method, path, token, BodyFor(method));

                Assert.True(
                    stranger.StatusCode == HttpStatusCode.NotFound &&
                    await ProblemCodeAsync(stranger) == "organization_not_found",
                    $"{method} {path} for a non-member answered {(int)stranger.StatusCode}.");
            }
        }

        // Nothing was written to the other organisation.
        Assert.Equal(1L, await RoomsOfOtherAsync());
        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" """));
        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Invitations" WHERE "OrganizationId" <> 1"""));
    }

    [Fact]
    public async Task UnknownOrganization_LooksLikeAnOrganizationYouAreNotIn()
    {
        using var response =
            await scenario.GetAsync($"/api/organizations/{Guid.NewGuid()}/rooms", scenario.OwnerToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "organization_not_found");
    }

    [Fact]
    public async Task UnregisteredAndDisabledUsers_AreRefused()
    {
        var unregistered = TokenFor("oid-unregistered", "new@example.com");

        using (var response = await scenario.GetAsync($"{LegacyPrefix}/rooms", unregistered))
        {
            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "user_not_registered");
        }

        await ExecuteAsync(
            scenario.Factory,
            """UPDATE "Users" SET "DisabledUtc" = 638100000000000000 WHERE "PublicId" = {0}""",
            scenario.MemberId.ToString().ToUpperInvariant());

        using (var response = await scenario.GetAsync($"{LegacyPrefix}/bookings", scenario.MemberToken))
        {
            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "user_disabled");
        }

        using (var response = await scenario.GetAsync("/api/bookings", scenario.MemberToken))
        {
            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "user_disabled");
        }
    }

    // Isolation between organisations

    [Fact]
    public async Task MemberOfOneOrganization_CannotSeeAnotherOrganizationsRoomsOrBookings()
    {
        var otherBooking =
            await scenario.CreateBookingAsync(
                scenario.OtherPrefix,
                scenario.OtherOwnerToken,
                scenario.BookingRequest(scenario.OtherRoomId));

        var otherBookingId = otherBooking.GetProperty("id").GetInt32();

        // Through the member's own organisation, the other organisation's IDs do not exist.
        using (var rooms = await scenario.GetAsync($"{LegacyPrefix}/rooms", scenario.MemberToken))
        {
            Assert.Equal(HttpStatusCode.OK, rooms.StatusCode);
            var names = (await JsonAsync(rooms)).EnumerateArray().Select(room => room.GetProperty("name").GetString()).ToArray();
            Assert.Equal(new string?[] { "Board Room", "Collaboration Room", "Focus Room" }, names);
        }

        using (var available =
                   await scenario.GetAsync(
                       $"{LegacyPrefix}/rooms/available?startUtc={Uri.EscapeDataString(scenario.Clock.UtcNow.AddDays(3).ToString("O"))}" +
                       $"&endUtc={Uri.EscapeDataString(scenario.Clock.UtcNow.AddDays(3).AddHours(1).ToString("O"))}",
                       scenario.MemberToken))
        {
            Assert.DoesNotContain(
                (await JsonAsync(available)).EnumerateArray(),
                room => room.GetProperty("id").GetInt32() == scenario.OtherRoomId);
        }

        using (var bookings = await scenario.GetAsync($"{LegacyPrefix}/bookings", scenario.MemberToken))
        {
            Assert.Empty((await JsonAsync(bookings)).EnumerateArray());
        }

        using (var booking = await scenario.GetAsync($"{LegacyPrefix}/bookings/{otherBookingId}", scenario.OwnerToken))
        {
            await AssertProblemAsync(booking, HttpStatusCode.NotFound, "booking_not_found");
        }

        using (var legacyBooking = await scenario.GetAsync($"/api/bookings/{otherBookingId}", scenario.OwnerToken))
        {
            await AssertProblemAsync(legacyBooking, HttpStatusCode.NotFound, "booking_not_found");
        }
    }

    [Fact]
    public async Task MemberOfOneOrganization_CannotChangeOrDeleteAnotherOrganizationsBookings()
    {
        var otherBookingId =
            (await scenario.CreateBookingAsync(
                scenario.OtherPrefix,
                scenario.OtherOwnerToken,
                scenario.BookingRequest(scenario.OtherRoomId)))
            .GetProperty("id").GetInt32();

        // Even the Legacy Owner, who may change every Legacy booking.
        using (var update =
                   await scenario.SendAsync(
                       HttpMethod.Put,
                       $"{LegacyPrefix}/bookings/{otherBookingId}",
                       scenario.OwnerToken,
                       scenario.BookingRequest(1, hours: 5)))
        {
            await AssertProblemAsync(update, HttpStatusCode.NotFound, "booking_not_found");
        }

        using (var delete = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/bookings/{otherBookingId}", scenario.OwnerToken))
        {
            await AssertProblemAsync(delete, HttpStatusCode.NotFound, "booking_not_found");
        }

        using (var legacyDelete = await scenario.SendAsync(HttpMethod.Delete, $"/api/bookings/{otherBookingId}", scenario.OwnerToken))
        {
            await AssertProblemAsync(legacyDelete, HttpStatusCode.NotFound, "booking_not_found");
        }

        // Booking the other organisation's room from Legacy.
        using (var create =
                   await scenario.SendAsync(
                       HttpMethod.Post,
                       $"{LegacyPrefix}/bookings",
                       scenario.OwnerToken,
                       scenario.BookingRequest(scenario.OtherRoomId, hours: 8)))
        {
            await AssertProblemAsync(create, HttpStatusCode.NotFound, "meeting_room_not_found");
        }

        // Moving a Legacy booking into the other organisation's room.
        var legacyBookingId =
            (await scenario.CreateBookingAsync(LegacyPrefix, scenario.OwnerToken, scenario.BookingRequest(1, hours: 10)))
            .GetProperty("id").GetInt32();

        using (var move =
                   await scenario.SendAsync(
                       HttpMethod.Put,
                       $"{LegacyPrefix}/bookings/{legacyBookingId}",
                       scenario.OwnerToken,
                       scenario.BookingRequest(scenario.OtherRoomId, hours: 10)))
        {
            await AssertProblemAsync(move, HttpStatusCode.NotFound, "meeting_room_not_found");
        }

        Assert.Equal(
            1L,
            await CountAsync(
                scenario.Factory,
                """SELECT COUNT(*) FROM "Bookings" WHERE "Id" = @p0 AND "UpdatedUtc" IS NULL AND "RoomId" = @p1""",
                otherBookingId,
                scenario.OtherRoomId));
    }

    // Booking rules by role

    [Fact]
    public async Task Members_CanBookAndChangeTheirOwnBookings_ButNotOthers()
    {
        var ownerBookingId =
            (await scenario.CreateBookingAsync(LegacyPrefix, scenario.OwnerToken, scenario.BookingRequest(1)))
            .GetProperty("id").GetInt32();

        var memberBooking =
            await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(2));

        Assert.True(memberBooking.GetProperty("createdByCurrentUser").GetBoolean());
        Assert.True(memberBooking.GetProperty("canModify").GetBoolean());

        var memberBookingId = memberBooking.GetProperty("id").GetInt32();

        using (var list = await scenario.GetAsync($"{LegacyPrefix}/bookings", scenario.MemberToken))
        {
            var bookings = (await JsonAsync(list)).EnumerateArray().ToArray();
            Assert.Equal(2, bookings.Length);
            Assert.False(bookings.Single(b => b.GetProperty("id").GetInt32() == ownerBookingId).GetProperty("canModify").GetBoolean());
        }

        using (var update =
                   await scenario.SendAsync(
                       HttpMethod.Put,
                       $"{LegacyPrefix}/bookings/{ownerBookingId}",
                       scenario.MemberToken,
                       scenario.BookingRequest(1, title: "Taken over")))
        {
            await AssertProblemAsync(update, HttpStatusCode.Forbidden, "booking_permission_denied");
        }

        using (var delete = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/bookings/{ownerBookingId}", scenario.MemberToken))
        {
            await AssertProblemAsync(delete, HttpStatusCode.Forbidden, "booking_permission_denied");
        }

        // The original endpoints apply the same rule to signed-in members.
        using (var legacyDelete = await scenario.SendAsync(HttpMethod.Delete, $"/api/bookings/{ownerBookingId}", scenario.MemberToken))
        {
            await AssertProblemAsync(legacyDelete, HttpStatusCode.Forbidden, "booking_permission_denied");
        }

        using (var ownUpdate =
                   await scenario.SendAsync(
                       HttpMethod.Put,
                       $"{LegacyPrefix}/bookings/{memberBookingId}",
                       scenario.MemberToken,
                       scenario.BookingRequest(2, title: "Renamed")))
        {
            Assert.Equal(HttpStatusCode.OK, ownUpdate.StatusCode);
            Assert.Equal("Renamed", (await JsonAsync(ownUpdate)).GetProperty("title").GetString());
        }

        using (var ownDelete = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/bookings/{memberBookingId}", scenario.MemberToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, ownDelete.StatusCode);
        }

        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" WHERE "Id" = @p0""", ownerBookingId));
    }

    [Fact]
    public async Task OwnersAndAdmins_CanChangeAndDeleteEveryBookingOfTheirOrganization()
    {
        var first =
            (await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1)))
            .GetProperty("id").GetInt32();

        var second =
            (await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(2)))
            .GetProperty("id").GetInt32();

        using (var adminUpdate =
                   await scenario.SendAsync(
                       HttpMethod.Put,
                       $"{LegacyPrefix}/bookings/{first}",
                       scenario.AdminToken,
                       scenario.BookingRequest(3, title: "Moved by admin")))
        {
            Assert.Equal(HttpStatusCode.OK, adminUpdate.StatusCode);
            var body = await JsonAsync(adminUpdate);
            Assert.Equal(3, body.GetProperty("roomId").GetInt32());
            Assert.False(body.GetProperty("createdByCurrentUser").GetBoolean());
            Assert.True(body.GetProperty("canModify").GetBoolean());
        }

        // Who changed it is recorded.
        Assert.Equal(
            1L,
            await CountAsync(
                scenario.Factory,
                """
                SELECT COUNT(*) FROM "Bookings" AS b JOIN "Users" AS u ON u."Id" = b."UpdatedByUserId"
                WHERE b."Id" = @p0 AND u."PublicId" = @p1 AND b."UpdatedUtc" IS NOT NULL
                """,
                first,
                scenario.AdminId.ToString().ToUpperInvariant()));

        using (var ownerDelete = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/bookings/{second}", scenario.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, ownerDelete.StatusCode);
        }
    }

    [Fact]
    public async Task BookingsFromBeforeSignIn_CanOnlyBeChangedByOwnersAndAdmins()
    {
        // A booking made through the anonymous endpoints has no creator.
        var start = scenario.Clock.UtcNow.AddDays(2);

        await ExecuteAsync(
            scenario.Factory,
            """
            INSERT INTO "Bookings" ("Id", "OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
            VALUES (4242, 1, 1, 'From before sign-in', 'Someone', {0}, {1}, {2})
            """,
            start.UtcDateTime.Ticks,
            start.AddHours(1).UtcDateTime.Ticks,
            scenario.Clock.UtcNow.UtcDateTime.Ticks);

        using (var memberDelete = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/bookings/4242", scenario.MemberToken))
        {
            await AssertProblemAsync(memberDelete, HttpStatusCode.Forbidden, "booking_permission_denied");
        }

        using (var read = await scenario.GetAsync($"{LegacyPrefix}/bookings/4242", scenario.OwnerToken))
        {
            var body = await JsonAsync(read);
            Assert.Equal("From before sign-in", body.GetProperty("title").GetString());
            Assert.True(body.GetProperty("canModify").GetBoolean());
        }

        using var ownerDelete = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/bookings/4242", scenario.OwnerToken);
        Assert.Equal(HttpStatusCode.NoContent, ownerDelete.StatusCode);
    }

    [Fact]
    public async Task NewBooking_IsOwnedByTheSignedInUser_AndBookedByDefaultsToTheirName()
    {
        var booking =
            await scenario.CreateBookingAsync(
                LegacyPrefix,
                scenario.MemberToken,
                scenario.BookingRequest(1, bookedBy: null));

        Assert.Equal("Mia Member", booking.GetProperty("bookedBy").GetString());

        Assert.Equal(
            1L,
            await CountAsync(
                scenario.Factory,
                """
                SELECT COUNT(*) FROM "Bookings" AS b JOIN "Users" AS u ON u."Id" = b."CreatedByUserId"
                WHERE b."Id" = @p0 AND u."PublicId" = @p1 AND b."OrganizationId" = 1
                """,
                booking.GetProperty("id").GetInt32(),
                scenario.MemberId.ToString().ToUpperInvariant()));
    }

    [Fact]
    public async Task OrganizationBookings_KeepTheExistingValidationAndOverlapRules()
    {
        await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1));

        using (var overlap =
                   await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/bookings", scenario.OwnerToken, scenario.BookingRequest(1, hours: 0.5)))
        {
            await AssertProblemAsync(overlap, HttpStatusCode.Conflict, "booking_overlap");
        }

        var past = scenario.Clock.UtcNow.AddHours(-1);

        using (var inThePast =
                   await scenario.SendAsync(
                       HttpMethod.Post,
                       $"{LegacyPrefix}/bookings",
                       scenario.OwnerToken,
                       new { roomId = 1, title = "Late", startUtc = past, endUtc = past.AddHours(2) }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, inThePast.StatusCode);
            Assert.True((await JsonAsync(inThePast)).GetProperty("errors").TryGetProperty("startUtc", out _));
        }

        using (var noTitle =
                   await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/bookings", scenario.OwnerToken, scenario.BookingRequest(2, title: " ")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);
        }
    }

    [Fact]
    public async Task ParallelBookingsOfTheSameRoom_CreateExactlyOne()
    {
        var tokens = new[] { scenario.OwnerToken, scenario.AdminToken, scenario.MemberToken };

        var responses =
            await Task.WhenAll(
                Enumerable.Range(0, 9).Select(
                    index =>
                        scenario.SendAsync(
                            HttpMethod.Post,
                            $"{LegacyPrefix}/bookings",
                            tokens[index % tokens.Length],
                            scenario.BookingRequest(2, hours: index * 0.1, title: $"Parallel {index}"))));

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(8, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" WHERE "RoomId" = 2"""));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task BookingList_CanBeLimitedToAPeriod()
    {
        await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1));
        await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1, hours: 48));

        var from = scenario.Clock.UtcNow.AddDays(2);

        using (var later = await scenario.GetAsync($"{LegacyPrefix}/bookings?fromUtc={Uri.EscapeDataString(from.ToString("O"))}", scenario.MemberToken))
        {
            Assert.Single((await JsonAsync(later)).EnumerateArray());
        }

        using var invalid =
            await scenario.GetAsync(
                $"{LegacyPrefix}/bookings?fromUtc={Uri.EscapeDataString(from.ToString("O"))}&toUtc={Uri.EscapeDataString(from.AddHours(-1).ToString("O"))}",
                scenario.MemberToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    // The original endpoints

    [Fact]
    public async Task OriginalEndpoints_RequireALegacyMember_WhenAnonymousAccessIsOff()
    {
        string[] paths =
        [
            "/api/rooms",
            "/api/rooms/available?startUtc=2030-01-01T09:00:00Z&endUtc=2030-01-01T10:00:00Z",
            "/api/bookings",
            "/api/bookings/1"
        ];

        foreach (var path in paths)
        {
            using var anonymous = await scenario.GetAsync(path, token: null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var outsider = await scenario.GetAsync(path, scenario.OutsiderToken);
            await AssertProblemAsync(outsider, HttpStatusCode.NotFound, "organization_not_found");

            using var otherOrganization = await scenario.GetAsync(path, scenario.OtherOwnerToken);
            await AssertProblemAsync(otherOrganization, HttpStatusCode.NotFound, "organization_not_found");
        }

        using (var create = await scenario.SendAsync(HttpMethod.Post, "/api/bookings", token: null, scenario.BookingRequest(1)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        }

        using (var memberRooms = await scenario.GetAsync("/api/rooms", scenario.MemberToken))
        {
            Assert.Equal(HttpStatusCode.OK, memberRooms.StatusCode);
            Assert.Equal(3, (await JsonAsync(memberRooms)).GetArrayLength());
        }

        using (var memberCreate = await scenario.SendAsync(HttpMethod.Post, "/api/bookings", scenario.MemberToken, scenario.BookingRequest(1)))
        {
            Assert.Equal(HttpStatusCode.Created, memberCreate.StatusCode);
        }

        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" WHERE "CreatedByUserId" IS NULL"""));
    }

    [Fact]
    public async Task ExistingLegacyBookings_KeepTheirIdsAndAreVisibleToLegacyMembers()
    {
        var start = scenario.Clock.UtcNow.AddDays(5);

        for (var id = 7; id <= 9; id++)
        {
            await ExecuteAsync(
                scenario.Factory,
                """
                INSERT INTO "Bookings" ("Id", "OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
                VALUES ({0}, 1, {1}, 'Existing', 'Before phase 3', {2}, {3}, {2})
                """,
                id,
                id - 6,
                start.UtcDateTime.Ticks,
                start.AddHours(1).UtcDateTime.Ticks);
        }

        using var organizationList = await scenario.GetAsync($"{LegacyPrefix}/bookings", scenario.MemberToken);
        using var legacyList = await scenario.GetAsync("/api/bookings", scenario.MemberToken);

        int[] expected = [7, 8, 9];

        Assert.Equal(expected, (await JsonAsync(organizationList)).EnumerateArray().Select(b => b.GetProperty("id").GetInt32()).Order());
        Assert.Equal(expected, (await JsonAsync(legacyList)).EnumerateArray().Select(b => b.GetProperty("id").GetInt32()).Order());
    }

    [Fact]
    public void AnonymousLegacyApi_IsRefusedOutsideDevelopmentAndTesting()
    {
        using var factory =
            new MeetingRoomBookingApiFactory()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("LegacyApi:AllowAnonymous", exception.ToString());
    }

    [Fact]
    public void AnonymousLegacyApi_IsOffByDefault()
    {
        Assert.False(new Features.Organizations.LegacyApiOptions().AllowAnonymous);

        var settings =
            JsonDocument.Parse(
                File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json")));

        Assert.False(
            settings.RootElement.TryGetProperty("LegacyApi", out var legacy) &&
            legacy.TryGetProperty("AllowAnonymous", out var allow) &&
            allow.GetBoolean());
    }

    // Helpers

    private (HttpMethod Method, string Path)[] OrganizationEndpoints(
        Guid organizationId)
    {
        var dataSource =
            scenario.Factory.Services.GetRequiredService<EndpointDataSource>();

        return
        [
            .. dataSource.Endpoints
                .OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/organizations/{organizationId", StringComparison.Ordinal) == true)
                .SelectMany(
                    endpoint =>
                        (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                        .Select(
                            method =>
                                (new HttpMethod(method),
                                 Fill(endpoint.RoutePattern.RawText!, organizationId))))
        ];
    }

    private static string Fill(
        string pattern,
        Guid organizationId) =>
        System.Text.RegularExpressions.Regex.Replace(
            pattern,
            @"\{(?<name>[A-Za-z]+)(:(?<constraint>[a-z]+))?\}",
            match =>
                match.Groups["name"].Value == "organizationId"
                    ? organizationId.ToString()
                    : match.Groups["constraint"].Value == "guid"
                        ? Guid.NewGuid().ToString()
                        : "1");

    private static object? BodyFor(
        HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put
            ? new { roomId = 1, title = "x", name = "x", capacity = 1, role = "Member", email = "x@example.com" }
            : null;

    private Task<long> RoomsOfOtherAsync() =>
        CountAsync(
            scenario.Factory,
            """SELECT COUNT(*) FROM "MeetingRooms" AS r JOIN "Organizations" AS o ON o."Id" = r."OrganizationId" WHERE o."PublicId" = @p0""",
            scenario.OtherOrganizationId.ToString().ToUpperInvariant());
}
