using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeetingRoomBooking.Api.IntegrationTests.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;

namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>
/// The product's documented journey, start to finish, as one story: a new
/// user creates an organisation and its rooms, invites an Admin and a Member,
/// and the members search, book and run into every rule on the way. Each
/// rule also has its own focused tests; this test shows that they work
/// together in the order a real organisation meets them.
/// </summary>
/// <remarks>
/// The real API on a temporary SQLite file, with tokens signed by the
/// test-only key (synthetic people, no real tenant). The anonymous demo mode
/// is off, as in production. The steps follow the demonstration sequence in
/// the README.
/// </remarks>
public sealed class DocumentedJourneyTests
    : IDisposable
{
    private readonly MeetingRoomBookingApiFactory baseFactory =
        new() { AllowAnonymousLegacyApi = false };

    private readonly WebApplicationFactory<Program> factory;

    private readonly string olivia = TokenFor("oid-olivia", "olivia@example.com", name: "Olivia Owner");
    private readonly string adam = TokenFor("oid-adam", "adam@example.com", name: "Adam Admin");
    private readonly string mia = TokenFor("oid-mia", "mia@example.com", name: "Mia Member");
    private readonly string oscar = TokenFor("oid-oscar", "oscar@example.com", name: "Oscar Outsider");

    public DocumentedJourneyTests()
    {
        factory = baseFactory.WithWebHostBuilder(builder => builder.UseEntraTestTokens());
    }

    [Fact]
    public async Task AnOrganisation_FromCreationToBooking_WithEveryRuleOnTheWay()
    {
        // 1. Sign-in registers the user once; registration grants nothing.
        using (var first = await RegisterAsync(factory, olivia))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        using (var again = await RegisterAsync(factory, olivia))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        Assert.Empty((await GetJsonAsync("/api/organizations", olivia)).EnumerateArray());

        // 2. Creating an organisation makes Olivia its Owner. A retry with the
        //    same Idempotency-Key returns the same organisation, not a second one.
        var key = Guid.NewGuid();
        Guid acme;

        using (var created = await SendAsync(HttpMethod.Post, "/api/organizations", olivia, new { name = "Acme Robotics" }, key))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await JsonAsync(created);
            acme = body.GetProperty("publicId").GetGuid();
            Assert.Equal("Owner", body.GetProperty("role").GetString());
        }

        using (var retried = await SendAsync(HttpMethod.Post, "/api/organizations", olivia, new { name = "Acme Robotics" }, key))
        {
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
            Assert.Equal(acme, (await JsonAsync(retried)).GetProperty("publicId").GetGuid());
        }

        var org = $"/api/organizations/{acme}";

        // 3. A new organisation has no rooms; the Owner creates them.
        var huddle = await CreateRoomAsync(org, olivia, "Huddle", 4);
        var boardroom = await CreateRoomAsync(org, olivia, "Boardroom", 10);

        using (var duplicate = await SendAsync(HttpMethod.Post, $"{org}/rooms", olivia, new { name = "huddle", capacity = 6 }))
        {
            await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "room_name_taken");
        }

        // 4. Invitations: the token plus the invited, verified e-mail.
        var adamInvitation = await InviteAsync(org, olivia, "adam@example.com", "Admin");
        var miaInvitation = await InviteAsync(org, olivia, "mia@example.com", "Member");

        await RegisterUserAsync(factory, adam);
        await RegisterUserAsync(factory, mia);
        await RegisterUserAsync(factory, oscar);

        using (var wrongPerson = await SendAsync(HttpMethod.Post, "/api/invitations/accept", oscar, new { token = miaInvitation }))
        {
            await AssertProblemAsync(wrongPerson, HttpStatusCode.Forbidden, "invitation_email_mismatch");
        }

        await AcceptAsync(adam, adamInvitation);
        await AcceptAsync(mia, miaInvitation);

        // An Admin may invite Members, never Admins.
        using (var adminInvitesAdmin = await SendAsync(HttpMethod.Post, $"{org}/invitations", adam, new { email = "new@example.com", role = "Admin" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, adminInvitesAdmin.StatusCode);
        }

        // 5. Search: the smallest free room that fits, for a future period.
        var start = baseFactory.Clock.UtcNow.AddDays(1).Date.AddHours(9);
        var period = new { startUtc = new DateTimeOffset(start, TimeSpan.Zero), endUtc = new DateTimeOffset(start.AddHours(1), TimeSpan.Zero) };

        var free = await GetJsonAsync($"{org}/rooms/available?startUtc={Iso(period.startUtc)}&endUtc={Iso(period.endUtc)}&minCapacity=6", mia);
        Assert.Equal([boardroom], free.EnumerateArray().Select(room => room.GetProperty("id").GetInt32()).ToArray());

        // 6. Booking with an Idempotency-Key: a retry after a lost answer
        //    returns the same booking; another person's overlap is refused.
        var bookingKey = Guid.NewGuid();
        var miaBooking = new { roomId = boardroom, title = "Customer demo", period.startUtc, period.endUtc };
        int miaBookingId;

        using (var booked = await SendAsync(HttpMethod.Post, $"{org}/bookings", mia, miaBooking, bookingKey))
        {
            Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
            var body = await JsonAsync(booked);
            miaBookingId = body.GetProperty("id").GetInt32();
            Assert.Equal("Mia Member", body.GetProperty("bookedBy").GetString());
        }

        using (var retried = await SendAsync(HttpMethod.Post, $"{org}/bookings", mia, miaBooking, bookingKey))
        {
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
            Assert.Equal(miaBookingId, (await JsonAsync(retried)).GetProperty("id").GetInt32());
        }

        using (var overlap = await SendAsync(HttpMethod.Post, $"{org}/bookings", olivia, miaBooking with { title = "Board meeting" }))
        {
            await AssertProblemAsync(overlap, HttpStatusCode.Conflict, "booking_overlap");
        }

        var oliviaBookingId =
            (await JsonAsync(await SendAsync(HttpMethod.Post, $"{org}/bookings", olivia, new { roomId = huddle, title = "Restructuring", period.startUtc, period.endUtc })))
            .GetProperty("id").GetInt32();

        // 7. Privacy: Mia sees that the Huddle is taken, not by whom or why.
        var miaView = (await GetJsonAsync($"{org}/bookings", mia)).EnumerateArray().ToArray();
        var hidden = miaView.Single(booking => booking.GetProperty("id").GetInt32() == oliviaBookingId);
        Assert.True(hidden.GetProperty("detailsHidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("title").ValueKind);
        Assert.Equal("Customer demo", miaView.Single(booking => booking.GetProperty("id").GetInt32() == miaBookingId).GetProperty("title").GetString());

        // 8. Role rules: a Member cannot touch others' bookings or manage rooms.
        using (var delete = await SendAsync(HttpMethod.Delete, $"{org}/bookings/{oliviaBookingId}", mia))
        {
            await AssertProblemAsync(delete, HttpStatusCode.Forbidden, "booking_permission_denied");
        }

        using (var room = await SendAsync(HttpMethod.Post, $"{org}/rooms", mia, new { name = "Mine", capacity = 2 }))
        {
            await AssertProblemAsync(room, HttpStatusCode.Forbidden, "organization_permission_denied");
        }

        // The Admin manages bookings and sees them in full.
        var adminView = (await GetJsonAsync($"{org}/bookings", adam)).EnumerateArray();
        Assert.All(adminView, booking => Assert.False(booking.GetProperty("detailsHidden").GetBoolean()));

        // 9. A role change applies to the next request.
        var miaId = (await GetJsonAsync("/api/me", mia)).GetProperty("user").GetProperty("publicId").GetGuid();
        await ChangeRoleAsync(org, olivia, miaId, "Admin");
        Assert.Equal("Restructuring", (await GetJsonAsync($"{org}/bookings/{oliviaBookingId}", mia)).GetProperty("title").GetString());
        await ChangeRoleAsync(org, olivia, miaId, "Member");

        // 10. Invariants: the only Owner cannot leave, and a room with an
        //     upcoming booking cannot be archived.
        var oliviaId = (await GetJsonAsync("/api/me", olivia)).GetProperty("user").GetProperty("publicId").GetGuid();

        using (var leave = await SendAsync(HttpMethod.Delete, $"{org}/members/{oliviaId}", olivia))
        {
            await AssertProblemAsync(leave, HttpStatusCode.Conflict, "last_owner");
        }

        using (var archive = await SendAsync(HttpMethod.Post, $"{org}/rooms/{boardroom}/archive", olivia))
        {
            await AssertProblemAsync(archive, HttpStatusCode.Conflict, "room_has_upcoming_bookings");
        }

        // 11. Tenant isolation: an outsider cannot tell the organisation exists.
        using (var outsider = await SendAsync(HttpMethod.Get, org, oscar))
        {
            await AssertProblemAsync(outsider, HttpStatusCode.NotFound, "organization_not_found");
        }

        // 12. A removed member loses access at once; their booking stays.
        using (var removed = await SendAsync(HttpMethod.Delete, $"{org}/members/{miaId}", olivia))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using (var afterRemoval = await SendAsync(HttpMethod.Get, $"{org}/bookings", mia))
        {
            await AssertProblemAsync(afterRemoval, HttpStatusCode.NotFound, "organization_not_found");
        }

        Assert.Equal("Customer demo", (await GetJsonAsync($"{org}/bookings/{miaBookingId}", olivia)).GetProperty("title").GetString());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string token,
        object? body = null,
        Guid? idempotencyKey = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (idempotencyKey is { } key)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private async Task<JsonElement> GetJsonAsync(
        string path,
        string token)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await JsonAsync(response);
    }

    private async Task<int> CreateRoomAsync(
        string org,
        string token,
        string name,
        int capacity)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{org}/rooms", token, new { name, capacity });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await JsonAsync(response)).GetProperty("id").GetInt32();
    }

    private async Task<string> InviteAsync(
        string org,
        string token,
        string email,
        string role)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{org}/invitations", token, new { email, role });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await JsonAsync(response)).GetProperty("token").GetString()!;
    }

    private async Task AcceptAsync(
        string token,
        string invitationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/invitations/accept", token, new { token = invitationToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task ChangeRoleAsync(
        string org,
        string token,
        Guid userId,
        string role)
    {
        using var response = await SendAsync(HttpMethod.Put, $"{org}/members/{userId}/role", token, new { role });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ProblemCodeAsync(response));
    }

    private static string Iso(
        DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToString("O"));

    public void Dispose()
    {
        factory.Dispose();
        baseFactory.Dispose();
    }
}
