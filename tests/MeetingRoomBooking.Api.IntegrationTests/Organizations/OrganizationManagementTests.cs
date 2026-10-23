using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeetingRoomBooking.Api.Data;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;
using static MeetingRoomBooking.Api.IntegrationTests.Organizations.TenantScenario;

namespace MeetingRoomBooking.Api.IntegrationTests.Organizations;

/// <summary>Creating organisations, and the rooms an Owner or Admin sets up in them.</summary>
public sealed class OrganizationManagementTests
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

    // Creating organisations

    [Fact]
    public async Task RegisteredUser_CreatesAnOrganization_AndBecomesItsOwner()
    {
        using var response = await CreateOrganizationAsync(scenario.OutsiderToken, "  Acme   Robotics ", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await JsonAsync(response);
        var id = body.GetProperty("publicId").GetGuid();

        Assert.Equal("Acme Robotics", body.GetProperty("name").GetString());
        Assert.Equal("Owner", body.GetProperty("role").GetString());
        Assert.False(body.GetProperty("isLegacy").GetBoolean());
        Assert.NotEqual(LegacyOrganization.PublicId, id);
        Assert.Equal($"/api/organizations/{id}", response.Headers.Location?.OriginalString);

        using var list = await scenario.GetAsync("/api/organizations", scenario.OutsiderToken);
        var organization = Assert.Single((await JsonAsync(list)).EnumerateArray());
        Assert.Equal(id, organization.GetProperty("publicId").GetGuid());
        Assert.Equal("Owner", organization.GetProperty("role").GetString());

        // /api/me shows it too, and the new organisation starts empty.
        using var me = await GetMeAsync(scenario.Factory, scenario.OutsiderToken);
        Assert.Single((await JsonAsync(me)).GetProperty("user").GetProperty("organizations").EnumerateArray());

        using var rooms = await scenario.GetAsync($"/api/organizations/{id}/rooms", scenario.OutsiderToken);
        Assert.Empty((await JsonAsync(rooms)).EnumerateArray());

        Assert.Equal(
            1L,
            await CountAsync(
                scenario.Factory,
                """
                SELECT COUNT(*) FROM "Memberships" AS m
                JOIN "Organizations" AS o ON o."Id" = m."OrganizationId"
                JOIN "Users" AS u ON u."Id" = m."UserId"
                WHERE o."PublicId" = @p0 AND m."Role" = 'Owner' AND m."CreatedByUserId" = u."Id" AND o."CreatedByUserId" = u."Id"
                """,
                id.ToString().ToUpperInvariant()));

        Assert.Equal(
            2L,
            await CountAsync(
                scenario.Factory,
                """
                SELECT COUNT(*) FROM "AuditEvents" AS a JOIN "Organizations" AS o ON o."Id" = a."OrganizationId"
                WHERE o."PublicId" = @p0 AND a."Type" IN ('OrganizationCreated', 'MembershipAdded') AND a."ActorUserId" IS NOT NULL
                """,
                id.ToString().ToUpperInvariant()));
    }

    [Fact]
    public async Task RepeatedRequest_WithTheSameKey_ReturnsTheSameOrganization()
    {
        var key = Guid.NewGuid();

        using var first = await CreateOrganizationAsync(scenario.OutsiderToken, "Acme", key);
        using var second = await CreateOrganizationAsync(scenario.OutsiderToken, "Acme", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(
            (await JsonAsync(first)).GetProperty("publicId").GetGuid(),
            (await JsonAsync(second)).GetProperty("publicId").GetGuid());

        Assert.Equal(1L, await OrganizationsNamedAsync("Acme"));
        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'OrganizationCreated'"""));
    }

    [Fact]
    public async Task ParallelRequests_WithTheSameKey_CreateOneOrganization()
    {
        var key = Guid.NewGuid();

        var responses =
            await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => CreateOrganizationAsync(scenario.OutsiderToken, "Parallel Ltd", key)));

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.OK));

            var ids = new HashSet<Guid>();
            foreach (var response in responses)
            {
                ids.Add((await JsonAsync(response)).GetProperty("publicId").GetGuid());
            }

            Assert.Single(ids);
            Assert.Equal(1L, await OrganizationsNamedAsync("Parallel Ltd"));
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
    public async Task TheSameKey_WithAnotherName_IsRefused()
    {
        var key = Guid.NewGuid();

        using var first = await CreateOrganizationAsync(scenario.OutsiderToken, "First", key);
        using var second = await CreateOrganizationAsync(scenario.OutsiderToken, "Second", key);

        await AssertProblemAsync(second, HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(0L, await OrganizationsNamedAsync("Second"));

        // The key belongs to its user: someone else may use the same value.
        using var other = await CreateOrganizationAsync(scenario.MemberToken, "Second", key);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrInvalidIdempotencyKey_IsRejected(
        string? key)
    {
        using var response = await CreateOrganizationAsync(scenario.OutsiderToken, "Acme", key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(0L, await OrganizationsNamedAsync("Acme"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    [InlineData("Bell\u0007sound")]
    [InlineData("Hidden\u202Eright-to-left")]
    public async Task InvalidNames_AreRejected(
        string name)
    {
        using var response = await CreateOrganizationAsync(scenario.OutsiderToken, name, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task TooLongName_IsRejected()
    {
        using var response = await CreateOrganizationAsync(scenario.OutsiderToken, new string('x', 101), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatingRequiresARegisteredSignedInUser()
    {
        using var anonymous = await CreateOrganizationAsync(token: null, "Acme", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var unregistered = await CreateOrganizationAsync(TokenFor("oid-new"), "Acme", Guid.NewGuid());
        await AssertProblemAsync(unregistered, HttpStatusCode.Forbidden, "user_not_registered");

        using var list = await scenario.GetAsync("/api/organizations", TokenFor("oid-new"));
        await AssertProblemAsync(list, HttpStatusCode.Forbidden, "user_not_registered");

        Assert.Equal(0L, await OrganizationsNamedAsync("Acme"));
    }

    [Fact]
    public async Task AUser_CanBeAMemberOfSeveralOrganizations()
    {
        using var created = await CreateOrganizationAsync(scenario.MemberToken, "Side Project", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var list = await scenario.GetAsync("/api/organizations", scenario.MemberToken);

        var organizations =
            (await JsonAsync(list)).EnumerateArray()
                .ToDictionary(o => o.GetProperty("name").GetString()!, o => o.GetProperty("role").GetString());

        Assert.Equal(2, organizations.Count);
        Assert.Equal("Member", organizations["Legacy"]);
        Assert.Equal("Owner", organizations["Side Project"]);

        // Owner of one organisation, still only a Member of the other.
        using var legacyRoom =
            await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.MemberToken, new { name = "Mine", capacity = 2 });
        await AssertProblemAsync(legacyRoom, HttpStatusCode.Forbidden, "organization_permission_denied");
    }

    [Fact]
    public async Task OrganizationCreation_IsLimitedPerDay()
    {
        for (var index = 0; index < 10; index++)
        {
            using var created = await CreateOrganizationAsync(scenario.OutsiderToken, $"Org {index}", Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var limited = await CreateOrganizationAsync(scenario.OutsiderToken, "One too many", Guid.NewGuid());
        await AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "organization_creation_limit");

        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddDays(1).AddMinutes(1);

        using var nextDay = await CreateOrganizationAsync(scenario.OutsiderToken, "One too many", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, nextDay.StatusCode);
    }

    [Fact]
    public async Task LegacyOrganization_KeepsItsRoomsAndOwner()
    {
        await CreateOrganizationAsync(scenario.OwnerToken, "Acme", Guid.NewGuid());

        using var legacy = await scenario.GetAsync(LegacyPrefix, scenario.OwnerToken);
        var body = await JsonAsync(legacy);

        Assert.Equal("Legacy", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("isLegacy").GetBoolean());
        Assert.Equal("Owner", body.GetProperty("role").GetString());
        Assert.Equal(3, body.GetProperty("memberCount").GetInt32());
        Assert.Equal(3, body.GetProperty("activeRoomCount").GetInt32());
    }

    // Organisation details and renaming

    [Fact]
    public async Task OnlyOwners_CanRenameAnOrganization_AndNeverTheLegacyOne()
    {
        var id = await CreatedOrganizationIdAsync(scenario.OutsiderToken, "Old Name");
        var path = $"/api/organizations/{id}";

        using (var renamed = await scenario.SendAsync(HttpMethod.Put, path, scenario.OutsiderToken, new { name = "New Name" }))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.Equal("New Name", (await JsonAsync(renamed)).GetProperty("name").GetString());
        }

        using (var legacy = await scenario.SendAsync(HttpMethod.Put, LegacyPrefix, scenario.OwnerToken, new { name = "Renamed" }))
        {
            await AssertProblemAsync(legacy, HttpStatusCode.Conflict, "legacy_organization_read_only");
        }

        using (var admin = await scenario.SendAsync(HttpMethod.Put, LegacyPrefix, scenario.AdminToken, new { name = "Renamed" }))
        {
            await AssertProblemAsync(admin, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'OrganizationRenamed'"""));
        Assert.Equal(1L, await OrganizationsNamedAsync("Legacy"));
    }

    // Rooms

    [Fact]
    public async Task OwnerOfANewOrganization_SetsUpRooms_AndMembersBookThem()
    {
        var id = await CreatedOrganizationIdAsync(scenario.OutsiderToken, "Fresh Start");
        var prefix = $"/api/organizations/{id}";

        using var created =
            await scenario.SendAsync(HttpMethod.Post, $"{prefix}/rooms", scenario.OutsiderToken, new { name = " Big   Hall ", capacity = 40 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var room = await JsonAsync(created);
        var roomId = room.GetProperty("id").GetInt32();

        Assert.Equal("Big Hall", room.GetProperty("name").GetString());
        Assert.Equal(40, room.GetProperty("capacity").GetInt32());
        Assert.Equal(JsonValueKind.Null, room.GetProperty("archivedUtc").ValueKind);

        var start = scenario.Clock.UtcNow.AddDays(1);

        using (var available =
                   await scenario.GetAsync(
                       $"{prefix}/rooms/available?startUtc={Uri.EscapeDataString(start.ToString("O"))}&endUtc={Uri.EscapeDataString(start.AddHours(1).ToString("O"))}&minCapacity=20",
                       scenario.OutsiderToken))
        {
            Assert.Equal(roomId, Assert.Single((await JsonAsync(available)).EnumerateArray()).GetProperty("id").GetInt32());
        }

        await scenario.CreateBookingAsync(prefix, scenario.OutsiderToken, scenario.BookingRequest(roomId));

        // The new room is invisible from Legacy.
        using var legacyBooking =
            await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/bookings", scenario.OwnerToken, scenario.BookingRequest(roomId, hours: 3));
        await AssertProblemAsync(legacyBooking, HttpStatusCode.NotFound, "meeting_room_not_found");

        Assert.Equal(
            1L,
            await CountAsync(
                scenario.Factory,
                """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'RoomCreated' AND "Details" LIKE @p0""",
                $"%\"roomId\":{roomId}%"));
    }

    [Fact]
    public async Task OwnersAndAdmins_ManageRooms_MembersOnlyUseThem()
    {
        using (var admin = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.AdminToken, new { name = "Admin Room", capacity = 3 }))
        {
            Assert.Equal(HttpStatusCode.Created, admin.StatusCode);
        }

        using (var member = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.MemberToken, new { name = "Member Room", capacity = 3 }))
        {
            await AssertProblemAsync(member, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var memberEdit = await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/rooms/1", scenario.MemberToken, new { name = "Hijacked", capacity = 3 }))
        {
            await AssertProblemAsync(memberEdit, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var memberArchive = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/archive", scenario.MemberToken))
        {
            await AssertProblemAsync(memberArchive, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var memberArchived = await scenario.GetAsync($"{LegacyPrefix}/rooms?includeArchived=true", scenario.MemberToken))
        {
            await AssertProblemAsync(memberArchived, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        using (var memberList = await scenario.GetAsync($"{LegacyPrefix}/rooms", scenario.MemberToken))
        {
            Assert.Equal(4, (await JsonAsync(memberList)).GetArrayLength());
        }

        Assert.Equal(0L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "MeetingRooms" WHERE "Name" IN ('Member Room', 'Hijacked')"""));
    }

    [Fact]
    public async Task RoomNames_AreUniquePerOrganization_IgnoringCase()
    {
        using (var duplicate = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.OwnerToken, new { name = "board ROOM", capacity = 5 }))
        {
            await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "room_name_taken");
        }

        using (var rename = await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/rooms/1", scenario.OwnerToken, new { name = "Board Room", capacity = 4 }))
        {
            await AssertProblemAsync(rename, HttpStatusCode.Conflict, "room_name_taken");
        }

        // Another organisation may use the same name.
        using var other =
            await scenario.SendAsync(HttpMethod.Post, $"{scenario.OtherPrefix}/rooms", scenario.OtherOwnerToken, new { name = "Board Room", capacity = 5 });
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData("  ", 4)]
    [InlineData("Room", 0)]
    [InlineData("Room", -1)]
    [InlineData("Room", 501)]
    [InlineData("Room", null)]
    public async Task InvalidRooms_AreRejected(
        string? name,
        int? capacity)
    {
        using var response =
            await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.OwnerToken, new { name, capacity });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EditingARoom_KeepsItsBookings()
    {
        var booking = await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1));

        using var edited =
            await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/rooms/1", scenario.AdminToken, new { name = "Focus Room 2", capacity = 6 });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        using var read = await scenario.GetAsync($"{LegacyPrefix}/bookings/{booking.GetProperty("id").GetInt32()}", scenario.MemberToken);
        Assert.Equal("Focus Room 2", (await JsonAsync(read)).GetProperty("roomName").GetString());

        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'RoomUpdated'"""));
    }

    [Fact]
    public async Task ArchivingARoom_KeepsPastBookings_AndRemovesItFromSearchAndBooking()
    {
        var booking = await scenario.CreateBookingAsync(LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1));
        var bookingId = booking.GetProperty("id").GetInt32();

        using (var refused = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/archive", scenario.OwnerToken))
        {
            await AssertProblemAsync(refused, HttpStatusCode.Conflict, "room_has_upcoming_bookings");
        }

        // Once the booking is over, the room can be archived.
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddDays(2);

        using (var archived = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/archive", scenario.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
            Assert.NotEqual(JsonValueKind.Null, (await JsonAsync(archived)).GetProperty("archivedUtc").ValueKind);
        }

        using (var again = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/archive", scenario.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        using (var list = await scenario.GetAsync($"{LegacyPrefix}/rooms", scenario.MemberToken))
        {
            Assert.DoesNotContain((await JsonAsync(list)).EnumerateArray(), room => room.GetProperty("id").GetInt32() == 1);
        }

        using (var legacyList = await scenario.GetAsync("/api/rooms", scenario.MemberToken))
        {
            Assert.DoesNotContain((await JsonAsync(legacyList)).EnumerateArray(), room => room.GetProperty("id").GetInt32() == 1);
        }

        using (var withArchived = await scenario.GetAsync($"{LegacyPrefix}/rooms?includeArchived=true", scenario.AdminToken))
        {
            Assert.Contains((await JsonAsync(withArchived)).EnumerateArray(), room => room.GetProperty("id").GetInt32() == 1);
        }

        var start = scenario.Clock.UtcNow.AddDays(1);

        using (var available =
                   await scenario.GetAsync(
                       $"{LegacyPrefix}/rooms/available?startUtc={Uri.EscapeDataString(start.ToString("O"))}&endUtc={Uri.EscapeDataString(start.AddHours(1).ToString("O"))}",
                       scenario.MemberToken))
        {
            Assert.DoesNotContain((await JsonAsync(available)).EnumerateArray(), room => room.GetProperty("id").GetInt32() == 1);
        }

        using (var book = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/bookings", scenario.OwnerToken, scenario.BookingRequest(1)))
        {
            await AssertProblemAsync(book, HttpStatusCode.NotFound, "meeting_room_not_found");
        }

        using (var edit = await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/rooms/1", scenario.OwnerToken, new { name = "x", capacity = 1 }))
        {
            await AssertProblemAsync(edit, HttpStatusCode.Conflict, "room_archived");
        }

        // The past booking is still there, in its archived room.
        using (var past = await scenario.GetAsync($"{LegacyPrefix}/bookings/{bookingId}", scenario.MemberToken))
        {
            Assert.Equal(HttpStatusCode.OK, past.StatusCode);
            Assert.Equal("Focus Room", (await JsonAsync(past)).GetProperty("roomName").GetString());
        }

        using (var restored = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/restore", scenario.AdminToken))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.Equal(JsonValueKind.Null, (await JsonAsync(restored)).GetProperty("archivedUtc").ValueKind);
        }

        Assert.Equal(
            2L,
            await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" IN ('RoomArchived', 'RoomRestored')"""));
    }

    [Fact]
    public async Task RestoringARoom_WhoseNameIsTakenAgain_IsRefused()
    {
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddMinutes(1);

        using (var archived = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/archive", scenario.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        }

        // An archived room frees its name.
        using (var replacement = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.OwnerToken, new { name = "Focus Room", capacity = 4 }))
        {
            Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        }

        using var restored = await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/1/restore", scenario.OwnerToken);
        await AssertProblemAsync(restored, HttpStatusCode.Conflict, "room_name_taken");
    }

    [Fact]
    public async Task RoomsOfAnotherOrganization_CannotBeManaged()
    {
        using var edit =
            await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/rooms/{scenario.OtherRoomId}", scenario.OwnerToken, new { name = "Mine", capacity = 2 });
        await AssertProblemAsync(edit, HttpStatusCode.NotFound, "meeting_room_not_found");

        using var archive =
            await scenario.SendAsync(HttpMethod.Post, $"{LegacyPrefix}/rooms/{scenario.OtherRoomId}/archive", scenario.OwnerToken);
        await AssertProblemAsync(archive, HttpStatusCode.NotFound, "meeting_room_not_found");

        Assert.Equal(1L, await CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "MeetingRooms" WHERE "Name" = @p0 AND "ArchivedUtc" IS NULL""", OtherRoomName));
    }

    // Helpers

    private async Task<HttpResponseMessage> CreateOrganizationAsync(
        string? token,
        string? name,
        object? idempotencyKey)
    {
        using var client = scenario.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/organizations");

        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString());
        }

        request.Content = JsonContent.Create(new { name });

        return await client.SendAsync(request);
    }

    private async Task<Guid> CreatedOrganizationIdAsync(
        string token,
        string name)
    {
        using var response = await CreateOrganizationAsync(token, name, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("publicId").GetGuid();
    }

    private Task<long> OrganizationsNamedAsync(
        string name) =>
        CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Organizations" WHERE "Name" = @p0""", name);
}
