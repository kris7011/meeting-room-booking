using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.EntraTestTokens;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;

namespace MeetingRoomBooking.Api.IntegrationTests.Authentication;

/// <summary>POST /api/me/registration, and GET /api/me staying read-only.</summary>
public sealed class RegistrationEndpointTests
    : IDisposable
{
    private readonly MeetingRoomBookingApiFactory baseFactory =
        new();

    private readonly WebApplicationFactory<Program> factory;

    public RegistrationEndpointTests()
    {
        factory = baseFactory.WithWebHostBuilder(builder => builder.UseEntraTestTokens());
    }

    public void Dispose()
    {
        factory.Dispose();
        baseFactory.Dispose();
    }

    private Task<long> UsersAsync() =>
        CountAsync(factory, """SELECT COUNT(*) FROM "Users" """);

    private Task<long> IdentitiesAsync() =>
        CountAsync(factory, """SELECT COUNT(*) FROM "UserIdentities" """);

    private Task<long> MembershipsAsync() =>
        CountAsync(factory, """SELECT COUNT(*) FROM "Memberships" """);

    [Fact]
    public async Task FirstRegistration_CreatesOneUserAndOneIdentityKeyedByIssuerAndObjectId()
    {
        using var response = await RegisterAsync(factory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("created").GetBoolean());

        var me = body.GetProperty("currentUser");
        Assert.Equal("Søren Ærø", me.GetProperty("displayName").GetString());
        Assert.Equal(TenantId, me.GetProperty("tenantId").GetString());

        var user = me.GetProperty("user");
        Assert.NotEqual(Guid.Empty, user.GetProperty("publicId").GetGuid());
        Assert.Equal("Søren Ærø", user.GetProperty("displayName").GetString());
        Assert.Equal(0, user.GetProperty("organizations").GetArrayLength());

        Assert.Equal(1L, await UsersAsync());
        Assert.Equal(
            1L,
            await CountAsync(
                factory,
                """
                SELECT COUNT(*) FROM "UserIdentities"
                WHERE "Provider" = 'Entra' AND "Issuer" = @p0 AND "Subject" = @p1 AND "LastUsedUtc" IS NOT NULL
                """,
                IssuerFor(TenantId),
                ObjectId));
        Assert.Equal(
            1L,
            await CountAsync(factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'UserRegistered' AND "OrganizationId" IS NULL"""));
    }

    [Fact]
    public async Task NewUser_IsAMemberOfNoOrganisation_NotEvenTheLegacyOne()
    {
        await RegisterUserAsync(factory, Create(UserClaims()));

        Assert.Equal(0L, await MembershipsAsync());

        using var me = await GetMeAsync(factory, Create(UserClaims()));
        var organizations = (await JsonAsync(me)).GetProperty("user").GetProperty("organizations");
        Assert.Equal(0, organizations.GetArrayLength());
    }

    [Fact]
    public async Task RepeatedRegistration_ReturnsTheSameUserAndCreatesNothing()
    {
        var first = await RegisterUserAsync(factory, Create(UserClaims()));

        using var again = await RegisterAsync(factory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await JsonAsync(again);
        Assert.False(body.GetProperty("created").GetBoolean());
        Assert.Equal(first, body.GetProperty("currentUser").GetProperty("user").GetProperty("publicId").GetGuid());

        Assert.Equal(1L, await UsersAsync());
        Assert.Equal(1L, await IdentitiesAsync());
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "AuditEvents" """));
    }

    [Fact]
    public async Task ParallelFirstRegistrations_CreateExactlyOneUser()
    {
        const int requests = 12;
        var token = Create(UserClaims());

        var responses =
            await Task.WhenAll(
                Enumerable.Range(0, requests)
                    .Select(_ => RegisterAsync(factory, token)));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(
                requests - 1,
                responses.Count(response => response.StatusCode == HttpStatusCode.OK));

            var publicIds = new HashSet<Guid>();

            foreach (var response in responses)
            {
                publicIds.Add(
                    (await JsonAsync(response))
                        .GetProperty("currentUser").GetProperty("user").GetProperty("publicId").GetGuid());
            }

            Assert.Single(publicIds);
            Assert.Equal(1L, await UsersAsync());
            Assert.Equal(1L, await IdentitiesAsync());
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
    public async Task ExistingUser_IsReturnedAndNotCreatedAgain()
    {
        await SeedUserAsync(disabled: false);

        using var response = await RegisterAsync(factory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(body.GetProperty("created").GetBoolean());

        var user = body.GetProperty("currentUser").GetProperty("user");
        Assert.Equal(SeededUserPublicId, user.GetProperty("publicId").GetGuid());
        Assert.Equal("Anna", user.GetProperty("displayName").GetString());

        Assert.Equal(1L, await UsersAsync());
        Assert.Equal(1L, await IdentitiesAsync());
        Assert.Equal(
            1L,
            await CountAsync(factory, """SELECT COUNT(*) FROM "Users" WHERE "LastSeenUtc" IS NOT NULL"""));
    }

    [Fact]
    public async Task DisabledUser_IsForbiddenAndNothingChanges()
    {
        await SeedUserAsync(disabled: true);

        using var response = await RegisterAsync(factory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("user_disabled", await ProblemCodeAsync(response));

        Assert.Equal(1L, await UsersAsync());
        Assert.Equal(
            1L,
            await CountAsync(factory, """SELECT COUNT(*) FROM "Users" WHERE "LastSeenUtc" IS NULL"""));
        Assert.Equal(
            1L,
            await CountAsync(factory, """SELECT COUNT(*) FROM "UserIdentities" WHERE "LastUsedUtc" IS NULL"""));
    }

    public static TheoryData<string?> RejectedTokens()
    {
        var wrongAudience = UserClaims();
        wrongAudience["aud"] = "11111111-2222-4333-8444-555555555555";

        var foreignIssuer = UserClaims();
        foreignIssuer["iss"] = $"https://evil.example/{TenantId}/v2.0";

        return new TheoryData<string?>
        {
            null,
            "not-a-jwt",
            Create(UserClaims(), expires: DateTime.UtcNow.AddMinutes(-5)),
            Create(UserClaims(), untrustedKey: true),
            Create(UserClaims(), symmetricKey: true),
            CreateUnsigned(UserClaims()),
            Create(wrongAudience),
            Create(foreignIssuer)
        };
    }

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task InvalidToken_IsUnauthorizedAndCreatesNothing(
        string? token)
    {
        using var response = await RegisterAsync(factory, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0L, await UsersAsync());
    }

    [Fact]
    public async Task TokenWithoutTheApiScope_IsForbiddenAndCreatesNothing()
    {
        var claims = UserClaims();
        claims["scp"] = "User.Read";

        using var response = await RegisterAsync(factory, Create(claims));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await UsersAsync());
    }

    [Fact]
    public async Task AppOnlyToken_CannotRegister()
    {
        var claims = UserClaims();
        claims.Remove("scp");
        claims["roles"] = new[] { "access_as_user" };
        claims["idtyp"] = "app";

        using var response = await RegisterAsync(factory, Create(claims));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await UsersAsync());
    }

    [Fact]
    public async Task GetMe_IsReadOnly_AndShowsTheUserOnlyAfterRegistration()
    {
        using var before = await GetMeAsync(factory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(before)).GetProperty("user").ValueKind);
        Assert.Equal(0L, await UsersAsync());

        var publicId = await RegisterUserAsync(factory, Create(UserClaims()));

        using var after = await GetMeAsync(factory, Create(UserClaims()));
        Assert.Equal(
            publicId,
            (await JsonAsync(after)).GetProperty("user").GetProperty("publicId").GetGuid());
    }

    [Fact]
    public async Task SameObjectIdInAnotherTenantOrAsAPersonalAccount_IsAnotherUser()
    {
        var work = await RegisterUserAsync(factory, Create(UserClaims()));
        var otherTenant = await RegisterUserAsync(factory, Create(UserClaims(OtherTenantId)));

        var personalClaims = PersonalAccountClaims();
        personalClaims["oid"] = ObjectId;
        var personal = await RegisterUserAsync(factory, Create(personalClaims));

        Assert.Equal(3, new HashSet<Guid> { work, otherTenant, personal }.Count);
        Assert.Equal(3L, await UsersAsync());
    }

    [Fact]
    public async Task SameVerifiedEmail_NeverLinksTwoIdentities()
    {
        var anna = await RegisterUserAsync(factory, TokenFor("oid-anna-work", "shared@example.com"));
        var other = await RegisterUserAsync(factory, TokenFor("oid-someone-else", "Shared@Example.com"));

        Assert.NotEqual(anna, other);
        Assert.Equal(2L, await UsersAsync());
        Assert.Equal(
            2L,
            await CountAsync(factory, """SELECT COUNT(*) FROM "UserIdentities" WHERE "VerifiedEmail" = 'shared@example.com'"""));
    }

    [Fact]
    public async Task EmailIsStoredAsVerifiedOnlyWhenMicrosoftVouchesForIt()
    {
        await RegisterUserAsync(factory, TokenFor("oid-verified", "  Anna@Example.COM "));
        await RegisterUserAsync(factory, TokenFor("oid-unverified", "bo@example.com", emailVerified: false));

        // preferred_username is never taken as an e-mail.
        await RegisterUserAsync(factory, TokenFor("oid-no-email"));

        Assert.Equal(
            1L,
            await CountAsync(
                factory,
                """SELECT COUNT(*) FROM "UserIdentities" WHERE "Subject" = 'oid-verified' AND "VerifiedEmail" = 'anna@example.com'"""));
        Assert.Equal(
            1L,
            await CountAsync(
                factory,
                """SELECT COUNT(*) FROM "Users" WHERE "ContactEmail" = 'anna@example.com'"""));
        Assert.Equal(
            2L,
            await CountAsync(
                factory,
                """SELECT COUNT(*) FROM "UserIdentities" WHERE "VerifiedEmail" IS NULL"""));
    }

    [Fact]
    public async Task VerifiedEmail_FollowsTheLatestToken()
    {
        await RegisterUserAsync(factory, TokenFor("oid-anna", "anna@example.com"));
        await RegisterUserAsync(factory, TokenFor("oid-anna", "anna@example.com", emailVerified: false));

        Assert.Equal(
            1L,
            await CountAsync(factory, """SELECT COUNT(*) FROM "UserIdentities" WHERE "VerifiedEmail" IS NULL"""));
    }

    [Fact]
    public async Task LongDisplayName_IsShortenedToFit()
    {
        var name = new string('x', 119) + "😀";

        using var response = await RegisterAsync(factory, TokenFor("oid-long", name: name));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored =
            (await JsonAsync(response)).GetProperty("currentUser").GetProperty("user").GetProperty("displayName").GetString();
        Assert.Equal(new string('x', 119), stored);
    }

    [Fact]
    public async Task WithoutEntraSettings_RegistrationIsUnavailableAndSaysWhy()
    {
        using var response = await RegisterAsync(baseFactory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("authentication_not_configured", await ProblemCodeAsync(response));
    }

    private static readonly Guid SeededUserPublicId =
        Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");

    private async Task SeedUserAsync(
        bool disabled)
    {
        // Starts the host, which creates and migrates the test database.
        using (factory.CreateClient())
        {
        }

        await ExecuteAsync(
            factory,
            disabled
                ? """
                  INSERT INTO "Users" ("Id", "PublicId", "DisplayName", "CreatedUtc", "DisabledUtc")
                  VALUES (1, 'A1B2C3D4-0000-4000-8000-000000000001', 'Anna', 638000000000000000, 638100000000000000)
                  """
                : """
                  INSERT INTO "Users" ("Id", "PublicId", "DisplayName", "CreatedUtc")
                  VALUES (1, 'A1B2C3D4-0000-4000-8000-000000000001', 'Anna', 638000000000000000)
                  """);

        await ExecuteAsync(
            factory,
            """
            INSERT INTO "UserIdentities" ("UserId", "Provider", "Issuer", "Subject", "VerifiedEmail", "CreatedUtc")
            VALUES (1, 'Entra', {0}, {1}, NULL, 638000000000000000)
            """,
            IssuerFor(TenantId),
            ObjectId);
    }
}
