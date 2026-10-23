using System.Net;
using System.Text.Json;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Organizations;
using MeetingRoomBooking.Api.IntegrationTests.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;

namespace MeetingRoomBooking.Api.IntegrationTests.Organizations;

/// <summary>
/// Two organisations on one isolated test database, with the anonymous
/// legacy API switched off (the API's default):
/// <list type="bullet">
/// <item>Legacy, with an Owner, an Admin and a Member who joined by invitation.</item>
/// <item>"Other Org", owned by someone who is not a member of Legacy, with one room.</item>
/// </list>
/// A registered outsider belongs to neither.
/// </summary>
public sealed class TenantScenario
    : IDisposable
{
    public const string OtherOrganizationName = "Other Org";
    public const string OtherRoomName = "Other Room";

    public static readonly string LegacyPrefix =
        $"/api/organizations/{LegacyOrganization.PublicId}";

    private readonly MeetingRoomBookingApiFactory baseFactory;

    private TenantScenario(
        MeetingRoomBookingApiFactory baseFactory)
    {
        this.baseFactory = baseFactory;
        Factory = baseFactory.WithWebHostBuilder(builder => builder.UseEntraTestTokens());
    }

    public WebApplicationFactory<Program> Factory { get; }

    public TestTimeProvider Clock =>
        baseFactory.Clock;

    public string OwnerToken { get; } =
        TokenFor("oid-owner", "owner@example.com", name: "Olivia Owner");

    public string AdminToken { get; } =
        TokenFor("oid-admin", "admin@example.com", name: "Adam Admin");

    public string MemberToken { get; } =
        TokenFor("oid-member", "member@example.com", name: "Mia Member");

    /// <summary>Owner of the other organisation; not a member of Legacy.</summary>
    public string OtherOwnerToken { get; } =
        TokenFor("oid-other-owner", "other@example.com", name: "Oscar Other");

    /// <summary>Registered, but a member of nothing.</summary>
    public string OutsiderToken { get; } =
        TokenFor("oid-outsider", "outsider@example.com", name: "Otto Outsider");

    public Guid OwnerId { get; private set; }

    public Guid MemberId { get; private set; }

    public Guid AdminId { get; private set; }

    public Guid OtherOrganizationId { get; private set; }

    public string OtherPrefix =>
        $"/api/organizations/{OtherOrganizationId}";

    public int OtherRoomId { get; private set; }

    public static async Task<TenantScenario> CreateAsync()
    {
        var scenario =
            new TenantScenario(
                new MeetingRoomBookingApiFactory { AllowAnonymousLegacyApi = false });

        try
        {
            await scenario.SetUpAsync();
            return scenario;
        }
        catch
        {
            scenario.Dispose();
            throw;
        }
    }

    private async Task SetUpAsync()
    {
        OwnerId = await RegisterUserAsync(Factory, OwnerToken);

        using (var scope = Factory.Services.CreateScope())
        {
            Assert.Equal(
                LegacyOwnerBootstrapOutcome.OwnerAdded,
                await LegacyOwnerBootstrap.ApplyAsync(
                    scope.ServiceProvider.GetRequiredService<BookingDbContext>(),
                    OwnerId.ToString(),
                    Clock,
                    NullLogger.Instance));
        }

        AdminId = await JoinAsync(LegacyOrganization.PublicId, OwnerToken, AdminToken, "admin@example.com", "Admin");
        MemberId = await JoinAsync(LegacyOrganization.PublicId, OwnerToken, MemberToken, "member@example.com", "Member");

        var otherOwnerId = await RegisterUserAsync(Factory, OtherOwnerToken);
        await RegisterUserAsync(Factory, OutsiderToken);

        OtherOrganizationId = Guid.NewGuid();

        await ExecuteAsync(
            Factory,
            """
            INSERT INTO "Organizations" ("PublicId", "Name", "IsLegacy", "CreatedUtc")
            VALUES ({0}, {1}, 0, 638000000000000000)
            """,
            OtherOrganizationId.ToString().ToUpperInvariant(),
            OtherOrganizationName);

        await ExecuteAsync(
            Factory,
            """
            INSERT INTO "Memberships" ("OrganizationId", "UserId", "Role", "CreatedUtc")
            SELECT o."Id", u."Id", 'Owner', 638000000000000000
            FROM "Organizations" AS o, "Users" AS u
            WHERE o."PublicId" = {0} AND u."PublicId" = {1}
            """,
            OtherOrganizationId.ToString().ToUpperInvariant(),
            otherOwnerId.ToString().ToUpperInvariant());

        await ExecuteAsync(
            Factory,
            """
            INSERT INTO "MeetingRooms" ("OrganizationId", "Name", "Capacity")
            SELECT "Id", {1}, 6 FROM "Organizations" WHERE "PublicId" = {0}
            """,
            OtherOrganizationId.ToString().ToUpperInvariant(),
            OtherRoomName);

        OtherRoomId =
            (int)await CountAsync(
                Factory,
                """SELECT "Id" FROM "MeetingRooms" WHERE "Name" = @p0""",
                OtherRoomName);
    }

    /// <summary>Invited by <paramref name="inviterToken"/>, registered and accepted. Returns the user's public ID.</summary>
    public async Task<Guid> JoinAsync(
        Guid organizationId,
        string inviterToken,
        string token,
        string email,
        string role)
    {
        using var invited =
            await SignedInRequests.SendAsync(
                Factory,
                HttpMethod.Post,
                $"/api/organizations/{organizationId}/invitations",
                inviterToken,
                new { email, role });

        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);

        var invitationToken =
            (await JsonAsync(invited)).GetProperty("token").GetString()!;

        var userId = await RegisterUserAsync(Factory, token);

        using var accepted =
            await SignedInRequests.SendAsync(
                Factory,
                HttpMethod.Post,
                "/api/invitations/accept",
                token,
                new { token = invitationToken });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        return userId;
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? token,
        object? body = null) =>
        SignedInRequests.SendAsync(Factory, method, path, token, body);

    public Task<HttpResponseMessage> GetAsync(
        string path,
        string? token) =>
        SendAsync(HttpMethod.Get, path, token);

    /// <summary>A booking request one day after the test clock's "now", shifted by <paramref name="hours"/>.</summary>
    public object BookingRequest(
        int roomId,
        double hours = 0,
        string title = "Planning",
        string? bookedBy = "Test person")
    {
        var start = Clock.UtcNow.AddDays(1).AddHours(hours);

        return new
        {
            roomId,
            title,
            bookedBy,
            startUtc = start,
            endUtc = start.AddHours(1)
        };
    }

    /// <summary>Creates a booking and returns its JSON (201 required).</summary>
    public async Task<JsonElement> CreateBookingAsync(
        string prefix,
        string token,
        object request)
    {
        using var response =
            await SendAsync(HttpMethod.Post, $"{prefix}/bookings", token, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await JsonAsync(response);
    }

    public static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ProblemCodeAsync(response));
    }

    public void Dispose()
    {
        Factory.Dispose();
        baseFactory.Dispose();
    }
}
