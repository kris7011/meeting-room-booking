using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Bookings;
using MeetingRoomBooking.Api.IntegrationTests.Organizations;
using Microsoft.AspNetCore.Mvc.Testing;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;
using static MeetingRoomBooking.Api.IntegrationTests.Organizations.TenantScenario;

namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>
/// Booking creation with an Idempotency-Key (ADR 0005): a client that lost
/// the answer repeats the request with the same key and gets the booking the
/// first request made, never a second one.
/// </summary>
/// <remarks>
/// Every test has its own temporary database. Races are either held at an
/// exact point with <see cref="TransactionGate"/> or asserted in a way that
/// holds for every interleaving; no test depends on timing.
/// </remarks>
public sealed class BookingIdempotencyTests
    : IAsyncLifetime
{
    private static readonly string Bookings =
        $"{LegacyPrefix}/bookings";

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

    // 1. Normal success

    [Fact]
    public async Task FirstRequest_CreatesTheBooking_AndRemembersItsKey()
    {
        using var response =
            await PostAsync(scenario.MemberToken, scenario.BookingRequest(1), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(1, await BookingCountAsync());
        Assert.Equal(1, await KeyCountAsync());
    }

    // 2. Identical retry

    [Fact]
    public async Task IdenticalRetry_ReturnsTheOriginalBooking()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);

        var created = await CreatedIdAsync(scenario.MemberToken, request, key);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var repeated = await PostAsync(scenario.MemberToken, request, key);

            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal("true", Assert.Single(repeated.Headers.GetValues("Idempotent-Replayed")));

            var body = await JsonAsync(repeated);
            Assert.Equal(created, body.GetProperty("id").GetInt32());
            Assert.Equal("Planning", body.GetProperty("title").GetString());
            Assert.True(body.GetProperty("createdByCurrentUser").GetBoolean());
        }

        Assert.Equal(1, await BookingCountAsync());
        Assert.Equal(1, await KeyCountAsync());
    }

    [Fact]
    public async Task Retry_WithTheSameInstantInAnotherOffset_IsTheSameRequest()
    {
        var key = Guid.NewGuid();
        var start = scenario.Clock.UtcNow.AddDays(1);
        var created = await CreatedIdAsync(scenario.MemberToken, Request(1, start), key);

        var copenhagen = TimeSpan.FromHours(2);

        using var repeated =
            await PostAsync(scenario.MemberToken, Request(1, start.ToOffset(copenhagen)), key);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(created, (await JsonAsync(repeated)).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Retry_AfterTheStartHasPassed_StillReturnsTheBooking()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1, hours: -22); // two hours from now
        var created = await CreatedIdAsync(scenario.MemberToken, request, key);

        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddHours(3);

        using var repeated = await PostAsync(scenario.MemberToken, request, key);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(created, (await JsonAsync(repeated)).GetProperty("id").GetInt32());
    }

    // 3. Same key, other request

    [Fact]
    public async Task SameKey_WithOtherDetails_IsRefused()
    {
        var key = Guid.NewGuid();
        await CreatedIdAsync(scenario.MemberToken, scenario.BookingRequest(1), key);

        foreach (var other in new[]
                 {
                     scenario.BookingRequest(1, title: "Another meeting"),
                     scenario.BookingRequest(2),
                     scenario.BookingRequest(1, hours: 3),
                     scenario.BookingRequest(1, bookedBy: "Someone else")
                 })
        {
            using var response = await PostAsync(scenario.MemberToken, other, key);

            await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        }

        Assert.Equal(1, await BookingCountAsync());
    }

    // 4. Concurrency

    [Fact]
    public async Task ConcurrentIdenticalRequests_CreateOneBooking()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);

        var responses =
            await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => PostAsync(scenario.MemberToken, request, key)));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.All(
                responses,
                response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));

            var ids = await Task.WhenAll(responses.Select(async response => (await JsonAsync(response)).GetProperty("id").GetInt32()));
            Assert.Single(ids.Distinct());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(1, await BookingCountAsync());
        Assert.Equal(1, await KeyCountAsync());
    }

    [Fact]
    public async Task RepeatSentWhileTheFirstHoldsTheWriteLock_GetsTheFirstBooking()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);

        // The first request has the write lock but has not looked up its key.
        Task<HttpResponseMessage> first;
        Task<HttpResponseMessage> second;

        using (var paused = scenario.Transactions.PauseNext(TransactionGatePoint.AfterBegin))
        {
            first = PostAsync(scenario.MemberToken, request, key);
            await paused.Reached;

            second = PostAsync(scenario.MemberToken, request, key);
        }

        using var firstResponse = await first;
        using var secondResponse = await second;

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(
            (await JsonAsync(firstResponse)).GetProperty("id").GetInt32(),
            (await JsonAsync(secondResponse)).GetProperty("id").GetInt32());
        Assert.Equal(1, await BookingCountAsync());
    }

    // 5. API restart

    [Fact]
    public async Task RetryAfterAnApiRestart_ReturnsTheOriginalBooking()
    {
        var databasePath =
            Path.Combine(Path.GetTempPath(), $"meeting-room-booking-tests-{Guid.NewGuid():N}.db");

        var key = Guid.NewGuid();
        var request = Request(1, MeetingRoomBookingApiFactory.DefaultNow.AddDays(3));
        int created;

        // The first API stops, and its memory with it; the file stays.
        using (var first = new MeetingRoomBookingApiFactory { DatabasePath = databasePath, KeepDatabaseOnDispose = true })
        {
            using var response = await PostAsync(first, "/api/bookings", token: null, request, key.ToString());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            created = (await JsonAsync(response)).GetProperty("id").GetInt32();
        }

        using var restarted = new MeetingRoomBookingApiFactory { DatabasePath = databasePath };
        using var repeated = await PostAsync(restarted, "/api/bookings", token: null, request, key.ToString());

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(created, (await JsonAsync(repeated)).GetProperty("id").GetInt32());
        Assert.Equal(1, await CountAsync(restarted, """SELECT COUNT(*) FROM "Bookings" """));
    }

    // 6. Connection loss

    [Fact]
    public async Task ResponseLostAfterTheCommit_RetryReturnsTheOriginalBooking()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);

        using (var connection = new CancellationTokenSource())
        {
            Task<HttpResponseMessage> lost;

            using (var paused = scenario.Transactions.PauseNext(TransactionGatePoint.AfterCommit))
            {
                lost = PostAsync(scenario.MemberToken, request, key, connection.Token);
                await paused.Reached;

                // Committed; the client gives up before any answer arrives.
                await connection.CancelAsync();
            }

            await DiscardAsync(lost);
        }

        Assert.Equal(1, await BookingCountAsync());

        using var repeated = await PostAsync(scenario.MemberToken, request, key);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(await OnlyBookingIdAsync(), (await JsonAsync(repeated)).GetProperty("id").GetInt32());
        Assert.Equal(1, await BookingCountAsync());
    }

    [Fact]
    public async Task ConnectionLostBeforeTheCommit_RetryLeavesExactlyOneBooking()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);

        using (var connection = new CancellationTokenSource())
        {
            Task<HttpResponseMessage> lost;

            using (var paused = scenario.Transactions.PauseNext(TransactionGatePoint.AfterBegin))
            {
                lost = PostAsync(scenario.MemberToken, request, key, connection.Token);
                await paused.Reached;

                // The write lock is held; nothing is committed yet.
                await connection.CancelAsync();
            }

            await DiscardAsync(lost);
        }

        // Whether the server finished the first request (then the retry
        // repeats it) or rolled it back (then the retry creates it), the
        // retry ends with exactly that one booking.
        using var retried = await PostAsync(scenario.MemberToken, request, key);

        Assert.Contains(retried.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        Assert.Equal(await OnlyBookingIdAsync(), (await JsonAsync(retried)).GetProperty("id").GetInt32());
        Assert.Equal(1, await KeyCountAsync());
    }

    // 7. Organisation isolation

    [Fact]
    public async Task Keys_AreScopedToTheOrganization()
    {
        // The other organisation's Owner is also a Member of Legacy.
        await scenario.JoinAsync(LegacyOrganization.PublicId, scenario.OwnerToken, scenario.OtherOwnerToken, "other@example.com", "Member");

        var key = Guid.NewGuid();
        var legacyRequest = scenario.BookingRequest(1, title: "Legacy secret");

        var legacyId = await CreatedIdAsync(scenario.OtherOwnerToken, legacyRequest, key);

        // The same key and body in the other organisation is a new request
        // there, for a room that organisation does not have.
        using (var crossed = await PostAsync(scenario.Factory, $"{scenario.OtherPrefix}/bookings", scenario.OtherOwnerToken, legacyRequest, key.ToString()))
        {
            await AssertProblemAsync(crossed, HttpStatusCode.NotFound, "meeting_room_not_found");
            Assert.DoesNotContain("Legacy secret", await crossed.Content.ReadAsStringAsync());
        }

        // With its own room it creates its own booking.
        using (var own = await PostAsync(scenario.Factory, $"{scenario.OtherPrefix}/bookings", scenario.OtherOwnerToken, scenario.BookingRequest(scenario.OtherRoomId), key.ToString()))
        {
            Assert.Equal(HttpStatusCode.Created, own.StatusCode);
            Assert.NotEqual(legacyId, (await JsonAsync(own)).GetProperty("id").GetInt32());
        }

        using var legacyRepeat = await PostAsync(scenario.OtherOwnerToken, legacyRequest, key);
        Assert.Equal(legacyId, (await JsonAsync(legacyRepeat)).GetProperty("id").GetInt32());
        Assert.Equal(2, await KeyCountAsync());
    }

    // 8. Different users

    [Fact]
    public async Task Keys_AreScopedToTheUser()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1, title: "Member's meeting");

        var memberId = await CreatedIdAsync(scenario.MemberToken, request, key);

        // Another member with the same key and body is not answered with the
        // member's booking: it is a new request, and the slot is taken.
        using (var same = await PostAsync(scenario.AdminToken, request, key))
        {
            Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
            var problem = await JsonAsync(same);
            Assert.Equal("booking_overlap", problem.GetProperty("code").GetString());
            Assert.False(problem.TryGetProperty("id", out _));
        }

        using (var other = await PostAsync(scenario.AdminToken, scenario.BookingRequest(2), key))
        {
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
            Assert.NotEqual(memberId, (await JsonAsync(other)).GetProperty("id").GetInt32());
        }

        using var memberRepeat = await PostAsync(scenario.MemberToken, request, key);
        Assert.Equal(memberId, (await JsonAsync(memberRepeat)).GetProperty("id").GetInt32());
    }

    // 9. Access revoked before the retry

    [Theory]
    [InlineData(MutationReauthorizationTests.Revocation.Removed)]
    [InlineData(MutationReauthorizationTests.Revocation.Disabled)]
    public async Task Retry_AfterAccessWasRevoked_IsRefusedWithoutTheBooking(
        MutationReauthorizationTests.Revocation revocation)
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1, title: "Before revocation");
        await CreatedIdAsync(scenario.MemberToken, request, key);

        await RevokeMemberAsync(revocation);

        using var repeated = await PostAsync(scenario.MemberToken, request, key);

        await AssertRevokedAsync(repeated, revocation);
        Assert.DoesNotContain("Before revocation", await repeated.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(MutationReauthorizationTests.Revocation.Removed)]
    [InlineData(MutationReauthorizationTests.Revocation.Disabled)]
    public async Task Retry_WhoseAccessIsRevokedWhileItWaitsForTheLock_IsRefused(
        MutationReauthorizationTests.Revocation revocation)
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1, title: "Before revocation");
        await CreatedIdAsync(scenario.MemberToken, request, key);

        // The retry has passed the access filter; the access goes before it
        // takes the write lock and looks up its key.
        HttpResponseMessage repeated;

        using (var paused = scenario.Transactions.PauseNext(TransactionGatePoint.BeforeBegin))
        {
            var pending = PostAsync(scenario.MemberToken, request, key);
            await paused.Reached;

            await RevokeMemberAsync(revocation);

            paused.Release();
            repeated = await pending;
        }

        using (repeated)
        {
            await AssertRevokedAsync(repeated, revocation);
            Assert.DoesNotContain("Before revocation", await repeated.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Retry_AfterARoleChange_AnswersWithTheCurrentRole()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);
        var created = await CreatedIdAsync(scenario.AdminToken, request, key);

        using (var demoted = await scenario.SendAsync(HttpMethod.Put, $"{LegacyPrefix}/members/{scenario.AdminId}/role", scenario.OwnerToken, new { role = "Member" }))
        {
            Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        }

        // Every member may book, so the former Admin still gets their booking.
        using var repeated = await PostAsync(scenario.AdminToken, request, key);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var body = await JsonAsync(repeated);
        Assert.Equal(created, body.GetProperty("id").GetInt32());
        Assert.True(body.GetProperty("canModify").GetBoolean());
    }

    // 10. Overlap with different keys

    [Fact]
    public async Task DifferentKeysForTheSameSlot_StillConflict()
    {
        var request = scenario.BookingRequest(1);
        await CreatedIdAsync(scenario.MemberToken, request, Guid.NewGuid());

        using (var otherKey = await PostAsync(scenario.MemberToken, request, Guid.NewGuid()))
        {
            await AssertProblemAsync(otherKey, HttpStatusCode.Conflict, "booking_overlap");
        }

        using (var noKey = await scenario.SendAsync(HttpMethod.Post, Bookings, scenario.MemberToken, request))
        {
            await AssertProblemAsync(noKey, HttpStatusCode.Conflict, "booking_overlap");
        }

        Assert.Equal(1, await BookingCountAsync());

        // A refused request stores no key.
        Assert.Equal(1, await KeyCountAsync());
    }

    // 11. Compatibility

    [Fact]
    public async Task RequestsWithoutAKey_BehaveAsBefore()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        var request = Request(1, MeetingRoomBookingApiFactory.DefaultNow.AddDays(3));

        using (var first = await PostAsync(factory, "/api/bookings", token: null, request, key: null))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        }

        using (var second = await PostAsync(factory, "/api/bookings", token: null, request, key: null))
        {
            await AssertProblemAsync(second, HttpStatusCode.Conflict, "booking_overlap");
        }

        Assert.Equal(0, await CountAsync(factory, """SELECT COUNT(*) FROM "BookingCreationRequests" """));
    }

    [Fact]
    public async Task AnonymousLegacyEndpoint_HonoursTheKey()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        var key = Guid.NewGuid().ToString();
        var request = Request(1, MeetingRoomBookingApiFactory.DefaultNow.AddDays(3));

        using var first = await PostAsync(factory, "/api/bookings", token: null, request, key);
        using var second = await PostAsync(factory, "/api/bookings", token: null, request, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", Assert.Single(second.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(
            (await JsonAsync(first)).GetProperty("id").GetInt32(),
            (await JsonAsync(second)).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task SignedInLegacyAndOrganizationEndpoints_ShareTheKey()
    {
        // Both create bookings in the legacy organisation for the same caller.
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);

        using var legacy = await PostAsync(scenario.Factory, "/api/bookings", scenario.MemberToken, request, key.ToString());
        Assert.Equal(HttpStatusCode.Created, legacy.StatusCode);

        using var organization = await PostAsync(scenario.MemberToken, request, key);
        Assert.Equal(HttpStatusCode.OK, organization.StatusCode);
        Assert.Equal(
            (await JsonAsync(legacy)).GetProperty("id").GetInt32(),
            (await JsonAsync(organization)).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("12345")]
    public async Task InvalidKey_IsRejected_AndNothingIsStored(
        string key)
    {
        using var response = await PostAsync(scenario.Factory, Bookings, scenario.MemberToken, scenario.BookingRequest(1), key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(0, await BookingCountAsync());
    }

    // 12. Audit

    [Fact]
    public async Task CreateAndRetries_AddNoAuditEvents()
    {
        var before = await AuditEventCountAsync();
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);

        await CreatedIdAsync(scenario.MemberToken, request, key);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var repeated = await PostAsync(scenario.MemberToken, request, key);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        }

        // Booking creation writes no audit events, with or without a key.
        Assert.Equal(before, await AuditEventCountAsync());
    }

    // Deletion and retention

    [Fact]
    public async Task Retry_AfterTheBookingWasDeleted_SaysItIsGone()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1);
        var created = await CreatedIdAsync(scenario.MemberToken, request, key);

        using (var deleted = await scenario.SendAsync(HttpMethod.Delete, $"{Bookings}/{created}", scenario.OwnerToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var repeated = await PostAsync(scenario.MemberToken, request, key);

        await AssertProblemAsync(repeated, HttpStatusCode.Gone, "created_booking_deleted");
        Assert.Equal(0, await BookingCountAsync());
        Assert.Null(await ScalarAsync(scenario.Factory, """SELECT "BookingId" FROM "BookingCreationRequests" """));
    }

    [Fact]
    public async Task ExpiredKey_IsForgotten_AndTheSlotStillConflicts()
    {
        var key = Guid.NewGuid();
        var request = scenario.BookingRequest(1, hours: 48);
        await CreatedIdAsync(scenario.MemberToken, request, key);

        scenario.Clock.UtcNow =
            scenario.Clock.UtcNow + BookingCreationRequest.RetentionPeriod + TimeSpan.FromMinutes(1);

        // Forgotten after the retention period; the original booking still
        // holds the slot, so the repeat cannot create a second one.
        using (var repeated = await PostAsync(scenario.MemberToken, request, key))
        {
            await AssertProblemAsync(repeated, HttpStatusCode.Conflict, "booking_overlap");
        }

        Assert.Equal(1, await BookingCountAsync());

        // A refused request stores nothing, its clean-up included; the next
        // successful keyed booking deletes the expired key.
        Assert.Equal(1, await KeyCountAsync());

        await CreatedIdAsync(scenario.MemberToken, scenario.BookingRequest(2, hours: 48), Guid.NewGuid());

        Assert.Equal(1, await KeyCountAsync());
        Assert.Equal(
            0,
            await CountAsync(
                scenario.Factory,
                """SELECT COUNT(*) FROM "BookingCreationRequests" WHERE "IdempotencyKey" = @p0""",
                key.ToString().ToUpperInvariant()));
    }

    [Fact]
    public async Task KeyWithinTheRetentionPeriod_IsKept()
    {
        await CreatedIdAsync(scenario.MemberToken, scenario.BookingRequest(1, hours: 48), Guid.NewGuid());

        scenario.Clock.UtcNow =
            scenario.Clock.UtcNow + BookingCreationRequest.RetentionPeriod - TimeSpan.FromMinutes(1);

        // Another keyed booking runs the clean-up.
        await CreatedIdAsync(scenario.MemberToken, scenario.BookingRequest(2, hours: 48), Guid.NewGuid());

        Assert.Equal(2, await KeyCountAsync());
    }

    // Helpers

    private static object Request(
        int roomId,
        DateTimeOffset start) =>
        new
        {
            roomId,
            title = "Planning",
            bookedBy = "Test person",
            startUtc = start,
            endUtc = start.AddHours(1)
        };

    private Task<HttpResponseMessage> PostAsync(
        string token,
        object body,
        Guid key,
        CancellationToken cancellationToken = default) =>
        PostAsync(scenario.Factory, Bookings, token, body, key.ToString(), cancellationToken);

    private static async Task<HttpResponseMessage> PostAsync(
        WebApplicationFactory<Program> factory,
        string path,
        string? token,
        object body,
        string? key,
        CancellationToken cancellationToken = default)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        request.Content = JsonContent.Create(body);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// The client has given up on this request: whatever arrives (a
    /// cancellation, or the answer the test server still delivers) is
    /// never read.
    /// </summary>
    private static async Task DiscardAsync(
        Task<HttpResponseMessage> abandoned)
    {
        try
        {
            (await abandoned).Dispose();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<int> CreatedIdAsync(
        string token,
        object body,
        Guid key)
    {
        using var response = await PostAsync(token, body, key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await JsonAsync(response)).GetProperty("id").GetInt32();
    }

    private async Task RevokeMemberAsync(
        MutationReauthorizationTests.Revocation revocation)
    {
        if (revocation == MutationReauthorizationTests.Revocation.Removed)
        {
            using var removed = await scenario.SendAsync(HttpMethod.Delete, $"{LegacyPrefix}/members/{scenario.MemberId}", scenario.OwnerToken);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            return;
        }

        await ExecuteAsync(
            scenario.Factory,
            """UPDATE "Users" SET "DisabledUtc" = 638100000000000000 WHERE "PublicId" = {0}""",
            scenario.MemberId.ToString().ToUpperInvariant());
    }

    private static Task AssertRevokedAsync(
        HttpResponseMessage response,
        MutationReauthorizationTests.Revocation revocation) =>
        revocation == MutationReauthorizationTests.Revocation.Removed
            ? AssertProblemAsync(response, HttpStatusCode.NotFound, "organization_not_found")
            : AssertProblemAsync(response, HttpStatusCode.Forbidden, "user_disabled");

    private Task<long> BookingCountAsync() =>
        CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "Bookings" """);

    private Task<long> KeyCountAsync() =>
        CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "BookingCreationRequests" """);

    private Task<long> AuditEventCountAsync() =>
        CountAsync(scenario.Factory, """SELECT COUNT(*) FROM "AuditEvents" """);

    private async Task<int> OnlyBookingIdAsync() =>
        Convert.ToInt32(await ScalarAsync(scenario.Factory, """SELECT "Id" FROM "Bookings" """));
}
