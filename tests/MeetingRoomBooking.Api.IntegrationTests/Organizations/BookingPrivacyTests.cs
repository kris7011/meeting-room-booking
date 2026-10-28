using System.Net;
using System.Text.Json;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;
using static MeetingRoomBooking.Api.IntegrationTests.Organizations.TenantScenario;

namespace MeetingRoomBooking.Api.IntegrationTests.Organizations;

/// <summary>
/// Members see when a room is booked, but not what another person's meeting
/// is about or who booked it; Owners and Admins see every booking in full
/// (<c>BookingActor.MaySeeDetails</c>). The role is read from the database
/// on every request, so a role change applies to the next one.
/// </summary>
public sealed class BookingPrivacyTests
    : IAsyncLifetime
{
    private const string OwnerTitle = "Confidential restructuring";
    private const string OwnerBookedBy = "Olivia Owner";
    private const string MemberTitle = "Member sync";

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

    [Fact]
    public async Task Member_SeesThatOthersBookedARoom_ButNotTheTitleOrWho()
    {
        var ownerBooking = await CreateOwnerBookingAsync();
        var memberBookingId = await CreateMemberBookingAsync();

        using var list =
            await scenario.GetAsync($"{LegacyPrefix}/bookings", scenario.MemberToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var body = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(OwnerTitle, body);
        Assert.DoesNotContain(OwnerBookedBy, body);

        var bookings = JsonDocument.Parse(body).RootElement.EnumerateArray().ToArray();

        var others = ById(bookings, ownerBooking.GetProperty("id").GetInt32());
        AssertHidden(others);
        Assert.Equal(1, others.GetProperty("roomId").GetInt32());
        Assert.Equal(ownerBooking.GetProperty("roomName").GetString(), others.GetProperty("roomName").GetString());
        Assert.Equal(ownerBooking.GetProperty("startUtc").GetDateTimeOffset(), others.GetProperty("startUtc").GetDateTimeOffset());
        Assert.Equal(ownerBooking.GetProperty("endUtc").GetDateTimeOffset(), others.GetProperty("endUtc").GetDateTimeOffset());
        Assert.False(others.GetProperty("canModify").GetBoolean());
        Assert.False(others.GetProperty("createdByCurrentUser").GetBoolean());

        var own = ById(bookings, memberBookingId);
        AssertShown(own, MemberTitle);
        Assert.True(own.GetProperty("createdByCurrentUser").GetBoolean());

        // Reading the single booking hides the same fields.
        using var single =
            await scenario.GetAsync($"{LegacyPrefix}/bookings/{ownerBooking.GetProperty("id").GetInt32()}", scenario.MemberToken);

        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        AssertHidden(await JsonAsync(single));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task OwnersAndAdmins_SeeEveryBookingInFull(
        string role)
    {
        var token = role == "owner" ? scenario.OwnerToken : scenario.AdminToken;
        var ownerBookingId = (await CreateOwnerBookingAsync()).GetProperty("id").GetInt32();
        var memberBookingId = await CreateMemberBookingAsync();

        using var list =
            await scenario.GetAsync($"{LegacyPrefix}/bookings", token);

        var bookings = (await JsonAsync(list)).EnumerateArray().ToArray();

        AssertShown(ById(bookings, ownerBookingId), OwnerTitle);
        AssertShown(ById(bookings, memberBookingId), MemberTitle);
        Assert.Equal("Mia Member", ById(bookings, memberBookingId).GetProperty("bookedBy").GetString());
    }

    [Fact]
    public async Task ARoleChange_AppliesToTheNextRequest()
    {
        var ownerBookingId = (await CreateOwnerBookingAsync()).GetProperty("id").GetInt32();
        var path = $"{LegacyPrefix}/bookings/{ownerBookingId}";

        using (var asMember = await scenario.GetAsync(path, scenario.MemberToken))
        {
            AssertHidden(await JsonAsync(asMember));
        }

        await ChangeMemberRoleAsync("Admin");

        using (var asAdmin = await scenario.GetAsync(path, scenario.MemberToken))
        {
            AssertShown(await JsonAsync(asAdmin), OwnerTitle);
        }

        await ChangeMemberRoleAsync("Member");

        using (var demoted = await scenario.GetAsync(path, scenario.MemberToken))
        {
            AssertHidden(await JsonAsync(demoted));
        }
    }

    [Fact]
    public async Task OriginalEndpoints_HideTheSameFields_FromSignedInMembers_InTheirFixedShape()
    {
        var ownerBookingId = (await CreateOwnerBookingAsync()).GetProperty("id").GetInt32();
        var memberBookingId = await CreateMemberBookingAsync();

        using var list =
            await scenario.GetAsync("/api/bookings", scenario.MemberToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var bookings = (await JsonAsync(list)).EnumerateArray().ToArray();
        var others = ById(bookings, ownerBookingId);

        Assert.Equal(JsonValueKind.Null, others.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, others.GetProperty("bookedBy").ValueKind);
        Assert.Equal(1, others.GetProperty("roomId").GetInt32());
        Assert.Equal(MemberTitle, ById(bookings, memberBookingId).GetProperty("title").GetString());

        // No property is added or removed: the original contract stays as it was.
        Assert.Equal(
            ["id", "roomId", "roomName", "title", "bookedBy", "startUtc", "endUtc", "createdUtc"],
            others.EnumerateObject().Select(property => property.Name).ToArray());

        using var single =
            await scenario.GetAsync($"/api/bookings/{ownerBookingId}", scenario.MemberToken);

        Assert.Equal(JsonValueKind.Null, (await JsonAsync(single)).GetProperty("title").ValueKind);

        using var asOwner =
            await scenario.GetAsync($"/api/bookings/{memberBookingId}", scenario.OwnerToken);

        Assert.Equal(MemberTitle, (await JsonAsync(asOwner)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task BookingsFromBeforeSignIn_AreHiddenFromMembers_AndShownToAdmins()
    {
        using var demoScenario =
            await TenantScenario.CreateAsync(allowAnonymousLegacyApi: true);

        // Anonymous demo mode: no creator is recorded, and everything is shown.
        using var created =
            await demoScenario.SendAsync(HttpMethod.Post, "/api/bookings", token: null, demoScenario.BookingRequest(3, title: "Before sign-in", bookedBy: "Demo user"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var bookingId = (await JsonAsync(created)).GetProperty("id").GetInt32();

        using (var anonymous = await demoScenario.GetAsync($"/api/bookings/{bookingId}", token: null))
        {
            Assert.Equal("Before sign-in", (await JsonAsync(anonymous)).GetProperty("title").GetString());
        }

        using (var member = await demoScenario.GetAsync($"{LegacyPrefix}/bookings/{bookingId}", demoScenario.MemberToken))
        {
            AssertHidden(await JsonAsync(member));
        }

        using (var admin = await demoScenario.GetAsync($"{LegacyPrefix}/bookings/{bookingId}", demoScenario.AdminToken))
        {
            AssertShown(await JsonAsync(admin), "Before sign-in");
        }
    }

    private async Task<JsonElement> CreateOwnerBookingAsync() =>
        await scenario.CreateBookingAsync(
            LegacyPrefix,
            scenario.OwnerToken,
            scenario.BookingRequest(1, title: OwnerTitle, bookedBy: OwnerBookedBy));

    /// <summary>"Booked by" left out, so the API fills in the member's name.</summary>
    private async Task<int> CreateMemberBookingAsync() =>
        (await scenario.CreateBookingAsync(
            LegacyPrefix,
            scenario.MemberToken,
            scenario.BookingRequest(2, title: MemberTitle, bookedBy: null)))
        .GetProperty("id").GetInt32();

    private async Task ChangeMemberRoleAsync(
        string role)
    {
        using var response =
            await scenario.SendAsync(
                HttpMethod.Put,
                $"{LegacyPrefix}/members/{scenario.MemberId}/role",
                scenario.OwnerToken,
                new { role });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static JsonElement ById(
        IEnumerable<JsonElement> bookings,
        int id) =>
        bookings.Single(booking => booking.GetProperty("id").GetInt32() == id);

    private static void AssertHidden(
        JsonElement booking)
    {
        Assert.True(booking.GetProperty("detailsHidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, booking.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, booking.GetProperty("bookedBy").ValueKind);
    }

    private static void AssertShown(
        JsonElement booking,
        string title)
    {
        Assert.False(booking.GetProperty("detailsHidden").GetBoolean());
        Assert.Equal(title, booking.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(booking.GetProperty("bookedBy").GetString()));
    }
}
