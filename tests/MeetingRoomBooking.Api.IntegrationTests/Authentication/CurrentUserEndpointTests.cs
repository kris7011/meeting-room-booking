using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeetingRoomBooking.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.EntraTestTokens;

namespace MeetingRoomBooking.Api.IntegrationTests.Authentication;

public sealed class CurrentUserEndpointTests
    : IDisposable
{
    private readonly MeetingRoomBookingApiFactory baseFactory =
        new();

    public void Dispose()
    {
        baseFactory.Dispose();
    }

    private WebApplicationFactory<Program> Factory(
        string keyIssuer = IssuerTemplate,
        params string[] allowedTenantIds)
    {
        return baseFactory.WithWebHostBuilder(
            builder =>
                builder.UseEntraTestTokens(
                    keyIssuer,
                    allowedTenantIds));
    }

    private static async Task<HttpResponseMessage> GetMeAsync(
        WebApplicationFactory<Program> factory,
        string? token)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");

        if (token is not null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(
        HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ValidToken_ReturnsTheIdentityFromTheToken()
    {
        using var response =
            await GetMeAsync(Factory(), Create(UserClaims()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await JsonAsync(response);
        Assert.Equal("Søren Ærø", body.GetProperty("displayName").GetString());
        Assert.Equal("soren@contoso.example", body.GetProperty("username").GetString());
        Assert.Equal(TenantId, body.GetProperty("tenantId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("user").ValueKind);
    }

    [Fact]
    public async Task ApplicationIdUriAudience_IsAccepted()
    {
        var claims = UserClaims();
        claims["aud"] = $"api://{ApiClientId}";

        using var response =
            await GetMeAsync(Factory(), Create(claims));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LinkedIdentity_ReturnsTheApiUser()
    {
        var factory = Factory();
        await SeedUserAsync(factory, disabled: false);

        using var response =
            await GetMeAsync(factory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var user = (await JsonAsync(response)).GetProperty("user");
        Assert.Equal("a1b2c3d4-0000-4000-8000-000000000001", user.GetProperty("publicId").GetString());
        Assert.Equal("Anna", user.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task IdentityOfAnotherTenant_IsNotTheSameUser()
    {
        var factory = Factory();
        await SeedUserAsync(factory, disabled: false);

        // Same oid, other tenant: a different identity (issuer differs).
        using var response =
            await GetMeAsync(factory, Create(UserClaims(OtherTenantId)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(response)).GetProperty("user").ValueKind);
    }

    [Fact]
    public async Task DisabledUser_IsForbidden()
    {
        var factory = Factory();
        await SeedUserAsync(factory, disabled: true);

        using var response =
            await GetMeAsync(factory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("user_disabled", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoToken_IsUnauthorized()
    {
        using var response = await GetMeAsync(Factory(), token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    public static TheoryData<string> RejectedTokens()
    {
        var wrongAudience = UserClaims();
        wrongAudience["aud"] = "11111111-2222-4333-8444-555555555555";

        var issuerOfAnotherTenant = UserClaims();
        issuerOfAnotherTenant["iss"] = IssuerFor(OtherTenantId);

        var v1Issuer = UserClaims();
        v1Issuer["iss"] = $"https://sts.windows.net/{TenantId}/";

        var foreignIssuer = UserClaims();
        foreignIssuer["iss"] = $"https://evil.example/{TenantId}/v2.0";

        var noTenant = UserClaims();
        noTenant.Remove("tid");

        var tenantNotAGuid = UserClaims();
        tenantNotAGuid["tid"] = "organizations";
        tenantNotAGuid["iss"] = IssuerFor("organizations");

        return new TheoryData<string>
        {
            Create(wrongAudience),
            Create(issuerOfAnotherTenant),
            Create(v1Issuer),
            Create(foreignIssuer),
            Create(noTenant),
            Create(tenantNotAGuid),
            Create(UserClaims(), expires: DateTime.UtcNow.AddMinutes(-5)),
            Create(UserClaims(), untrustedKey: true),
            Create(UserClaims(), symmetricKey: true),
            CreateUnsigned(UserClaims()),
            "not-a-jwt"
        };
    }

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task InvalidToken_IsUnauthorized(
        string token)
    {
        using var response = await GetMeAsync(Factory(), token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredWithinClockSkew_IsAccepted()
    {
        using var response =
            await GetMeAsync(Factory(), Create(UserClaims(), expires: DateTime.UtcNow.AddSeconds(-30)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SigningKeyPublishedForAnotherTenant_IsRejected()
    {
        using var response =
            await GetMeAsync(
                Factory(keyIssuer: IssuerFor(OtherTenantId)),
                Create(UserClaims()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SigningKeyPublishedForTheTokensTenant_IsAccepted()
    {
        using var response =
            await GetMeAsync(
                Factory(keyIssuer: IssuerFor(TenantId)),
                Create(UserClaims()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AllowedTenants_RejectOtherTenants()
    {
        var factory = Factory(IssuerTemplate, OtherTenantId);

        using var rejected = await GetMeAsync(factory, Create(UserClaims(TenantId)));
        using var accepted = await GetMeAsync(factory, Create(UserClaims(OtherTenantId)));

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Theory]
    [InlineData("User.Read")]
    [InlineData("access_as_user_extra")]
    [InlineData("")]
    public async Task TokenWithoutTheApiScope_IsForbidden(
        string scope)
    {
        var claims = UserClaims();
        claims["scp"] = scope;

        using var response = await GetMeAsync(Factory(), Create(claims));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ScopeAmongOthers_IsAccepted()
    {
        var claims = UserClaims();
        claims["scp"] = "Other.Scope access_as_user";

        using var response = await GetMeAsync(Factory(), Create(claims));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AppOnlyToken_IsForbidden()
    {
        // Client credentials: roles instead of scp, and no user.
        var claims = UserClaims();
        claims.Remove("scp");
        claims.Remove("name");
        claims.Remove("preferred_username");
        claims["roles"] = new[] { "access_as_user" };
        claims["idtyp"] = "app";

        using var response = await GetMeAsync(Factory(), Create(claims));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithoutObjectId_IsForbidden()
    {
        var claims = UserClaims();
        claims.Remove("oid");

        using var response = await GetMeAsync(Factory(), Create(claims));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithoutEntraSettings_MeIsUnavailableAndSaysWhy()
    {
        using var response = await GetMeAsync(baseFactory, Create(UserClaims()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "authentication_not_configured",
            (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingEndpoints_StayAnonymous_WithOrWithoutAnInvalidToken(
        bool configured)
    {
        var factory = configured ? Factory() : baseFactory;
        using var client = factory.CreateClient();

        using var anonymous = await client.GetAsync("/api/rooms");

        using var withBadToken = new HttpRequestMessage(HttpMethod.Get, "/api/rooms");
        withBadToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        using var badToken = await client.SendAsync(withBadToken);

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, badToken.StatusCode);
    }

    private static async Task SeedUserAsync(
        WebApplicationFactory<Program> factory,
        bool disabled)
    {
        // Starts the host, which creates and migrates the test database.
        using (factory.CreateClient())
        {
        }

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<BookingDbContext>().Database;

        // DisabledUtc in UTC ticks, like every timestamp column.
        await database.ExecuteSqlRawAsync(
            disabled
                ? """
                  INSERT INTO "Users" ("Id", "PublicId", "DisplayName", "CreatedUtc", "DisabledUtc")
                  VALUES (1, 'A1B2C3D4-0000-4000-8000-000000000001', 'Anna', 638000000000000000, 638100000000000000)
                  """
                : """
                  INSERT INTO "Users" ("Id", "PublicId", "DisplayName", "CreatedUtc")
                  VALUES (1, 'A1B2C3D4-0000-4000-8000-000000000001', 'Anna', 638000000000000000)
                  """);

        await database.ExecuteSqlRawAsync(
            """
            INSERT INTO "UserIdentities" ("UserId", "Provider", "Issuer", "Subject", "VerifiedEmail", "CreatedUtc")
            VALUES (1, 'Entra', {0}, {1}, NULL, 638000000000000000)
            """,
            IssuerFor(TenantId),
            ObjectId);
    }
}
