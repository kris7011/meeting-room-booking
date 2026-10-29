using System.Net;
using System.Net.Http.Json;
using MeetingRoomBooking.Api.Features.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;

namespace MeetingRoomBooking.Api.IntegrationTests.Authentication;

/// <summary>
/// E-mail and one-time passcode sign-in through Microsoft Entra External ID:
/// its tokens are accepted next to Entra ID's, each by its own scheme, and
/// never mistaken for each other.
/// </summary>
public sealed class ExternalIdAuthenticationTests
    : IDisposable
{
    private readonly MeetingRoomBookingApiFactory baseFactory =
        new();

    public void Dispose()
    {
        baseFactory.Dispose();
    }

    private WebApplicationFactory<Program> Factory(
        bool emailClaimIsVerified = false)
    {
        return baseFactory.WithWebHostBuilder(
            builder => builder.UseExternalIdTestTokens(emailClaimIsVerified));
    }

    [Fact]
    public async Task ExternalIdToken_IsAccepted_AndNamesItsProvider()
    {
        using var response =
            await GetMeAsync(Factory(), ExternalIdTestTokens.TokenFor(ExternalIdTestTokens.ObjectId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await JsonAsync(response);
        Assert.Equal("ExternalId", body.GetProperty("provider").GetString());
        Assert.Equal(ExternalIdTestTokens.TenantId, body.GetProperty("tenantId").GetString());
        Assert.Equal("Emma E-mail", body.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task EntraToken_StillWorks_WithExternalIdConfigured()
    {
        using var response =
            await GetMeAsync(Factory(), EntraTestTokens.Create(EntraTestTokens.UserClaims()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Entra", (await JsonAsync(response)).GetProperty("provider").GetString());
    }

    [Fact]
    public async Task EntraToken_ReportsEntra_WithoutExternalId()
    {
        using var factory =
            baseFactory.WithWebHostBuilder(builder => builder.UseEntraTestTokens());

        using var response =
            await GetMeAsync(factory, EntraTestTokens.Create(EntraTestTokens.UserClaims()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Entra", (await JsonAsync(response)).GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Registration_StoresAnExternalIdIdentity_KeyedByIssuerAndObjectId()
    {
        var factory = Factory();

        await RegisterUserAsync(factory, ExternalIdTestTokens.TokenFor("e-mail-user"));

        Assert.Equal(
            1L,
            await CountAsync(
                factory,
                """SELECT COUNT(*) FROM "UserIdentities" WHERE "Provider" = 'ExternalId' AND "Issuer" = @p0 AND "Subject" = 'e-mail-user'""",
                ExternalIdTestTokens.Issuer));

        Assert.Equal(
            "ExternalId",
            await ScalarAsync(
                factory,
                """SELECT json_extract("Details", '$.provider') FROM "AuditEvents" WHERE "Type" = 'UserRegistered'"""));

        // Signing in again finds the same user.
        using var again = await RegisterAsync(factory, ExternalIdTestTokens.TokenFor("e-mail-user"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "Users" """));
    }

    [Fact]
    public async Task SameObjectId_FromBothIssuers_IsTwoUsers()
    {
        // Object IDs are unique per tenant only; the issuer is part of the key.
        var factory = Factory();
        const string objectId = "12345678-90ab-4cde-8f01-234567890abc";

        var entraUser = await RegisterUserAsync(factory, TokenFor(objectId));
        var externalUser = await RegisterUserAsync(factory, ExternalIdTestTokens.TokenFor(objectId));

        Assert.NotEqual(entraUser, externalUser);
        Assert.Equal(2L, await CountAsync(factory, """SELECT COUNT(*) FROM "UserIdentities" WHERE "Subject" = @p0""", objectId));
    }

    [Fact]
    public async Task SameEmail_OnBothIssuers_IsNeverLinked()
    {
        var factory = Factory(emailClaimIsVerified: true);

        var entraUser = await RegisterUserAsync(factory, TokenFor("oid-entra", "same@example.com"));
        var externalUser = await RegisterUserAsync(factory, ExternalIdTestTokens.TokenFor("oid-external", "same@example.com"));

        Assert.NotEqual(entraUser, externalUser);
    }

    public static TheoryData<string, Func<Dictionary<string, object>, string>> RejectedTokens =>
        new()
        {
            {
                "audience of the Entra registration",
                claims =>
                {
                    claims["aud"] = EntraTestTokens.ApiClientId;
                    return ExternalIdTestTokens.Create(claims);
                }
            },
            {
                "another external tenant's issuer",
                claims =>
                {
                    claims["iss"] = ExternalIdTestTokens.IssuerFor(ExternalIdTestTokens.OtherTenantId);
                    claims["tid"] = ExternalIdTestTokens.OtherTenantId;
                    return ExternalIdTestTokens.Create(claims);
                }
            },
            {
                "tid of another tenant",
                claims =>
                {
                    claims["tid"] = ExternalIdTestTokens.OtherTenantId;
                    return ExternalIdTestTokens.Create(claims);
                }
            },
            {
                "no tid",
                claims =>
                {
                    claims.Remove("tid");
                    return ExternalIdTestTokens.Create(claims);
                }
            },
            {
                "signed with the Entra key",
                claims => EntraTestTokens.Create(claims)
            },
            {
                "expired",
                claims => ExternalIdTestTokens.Create(claims, DateTime.UtcNow.AddHours(-1))
            },
            {
                "unsigned",
                EntraTestTokens.CreateUnsigned
            }
        };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task InvalidExternalIdToken_IsRejected(
        string reason,
        Func<Dictionary<string, object>, string> create)
    {
        using var response =
            await GetMeAsync(Factory(), create(ExternalIdTestTokens.UserClaims()));

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, reason);
    }

    [Fact]
    public async Task ExternalIdToken_WithoutTheApiScope_IsForbidden()
    {
        var claims = ExternalIdTestTokens.UserClaims();
        claims["scp"] = "User.Read";

        using var response = await GetMeAsync(Factory(), ExternalIdTestTokens.Create(claims));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExternalIdIssuer_SignedWithTheEntraKey_IsNotAcceptedByEntra()
    {
        // An Entra-signed token claiming a ciamlogin.com issuer goes to the
        // External ID scheme, whose keys do not include Entra's; and Entra's
        // own scheme would reject the issuer.
        var claims = EntraTestTokens.UserClaims();
        claims["iss"] = ExternalIdTestTokens.Issuer;

        using var response = await GetMeAsync(Factory(), EntraTestTokens.Create(claims));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExternalIdToken_IsRejected_WhenExternalIdIsNotConfigured()
    {
        using var factory =
            baseFactory.WithWebHostBuilder(builder => builder.UseEntraTestTokens());

        using var response =
            await GetMeAsync(factory, ExternalIdTestTokens.TokenFor(ExternalIdTestTokens.ObjectId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // E-mail

    [Fact]
    public async Task EmailClaim_IsNotVerified_ByDefault()
    {
        var factory = Factory();

        await RegisterUserAsync(factory, ExternalIdTestTokens.TokenFor("oid-email", "Emma@Example.com"));

        Assert.Null(await ScalarAsync(factory, """SELECT "VerifiedEmail" FROM "UserIdentities" """));
        Assert.Null(await ScalarAsync(factory, """SELECT "ContactEmail" FROM "Users" """));
    }

    [Fact]
    public async Task EmailClaim_IsVerified_WhenConfigured()
    {
        var factory = Factory(emailClaimIsVerified: true);

        await RegisterUserAsync(factory, ExternalIdTestTokens.TokenFor("oid-email", " Emma@Example.com "));

        Assert.Equal("emma@example.com", await ScalarAsync(factory, """SELECT "VerifiedEmail" FROM "UserIdentities" """));
    }

    [Fact]
    public async Task TokenCannotMarkItsOwnEmailAsVerified()
    {
        // The API's own marker claim, put into the token by its issuer, is
        // ignored: only the identity the API adds counts.
        var factory = Factory();
        var claims = ExternalIdTestTokens.UserClaims("oid-forged");
        claims["email"] = "victim@example.com";
        claims[ExternalIdAuthentication.VerifiedEmailClaim] = "victim@example.com";
        claims["xms_edov"] = true;

        await RegisterUserAsync(factory, ExternalIdTestTokens.Create(claims));

        Assert.Null(await ScalarAsync(factory, """SELECT "VerifiedEmail" FROM "UserIdentities" """));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task Invitation_NeedsAVerifiedEmail_ForAnExternalIdUser(
        bool emailClaimIsVerified,
        HttpStatusCode expected)
    {
        var factory = Factory(emailClaimIsVerified);
        var ownerToken = TokenFor("oid-owner", "owner@example.com");
        var inviteeToken = ExternalIdTestTokens.TokenFor("oid-invitee", "invitee@example.com");
        await RegisterUserAsync(factory, ownerToken);
        await RegisterUserAsync(factory, inviteeToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/organizations");
        request.Headers.Authorization = new("Bearer", ownerToken);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { name = "Acme" });
        using var client = factory.CreateClient();
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var organizationId = (await JsonAsync(created)).GetProperty("publicId").GetGuid();

        using var invited =
            await SendAsync(
                factory,
                HttpMethod.Post,
                $"/api/organizations/{organizationId}/invitations",
                ownerToken,
                new { email = "invitee@example.com", role = "Member" });
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        var invitation = (await JsonAsync(invited)).GetProperty("token").GetString();

        using var accepted =
            await SendAsync(factory, HttpMethod.Post, "/api/invitations/accept", inviteeToken, new { token = invitation });

        Assert.Equal(expected, accepted.StatusCode);

        if (expected == HttpStatusCode.Forbidden)
        {
            Assert.Equal("verified_email_required", await ProblemCodeAsync(accepted));
        }
    }

    // Configuration

    [Theory]
    [InlineData("ExternalId:ApiClientId", "my-api", "ApiClientId")]
    [InlineData("ExternalId:TenantId", "roombookingtest.onmicrosoft.com", "TenantId")]
    [InlineData("ExternalId:TenantSubdomain", "roombookingtest.ciamlogin.com", "TenantSubdomain")]
    [InlineData("ExternalId:TenantSubdomain", "", "TenantSubdomain")]
    public void InvalidSettings_StopTheApiFromStarting(
        string key,
        string value,
        string expectedSetting)
    {
        using var factory =
            baseFactory.WithWebHostBuilder(
                builder =>
                {
                    builder.UseExternalIdTestTokens();
                    builder.UseSetting(key, value);
                });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains($"ExternalId:{expectedSetting}", error.Message);
    }

    [Fact]
    public void ExternalIdWithoutEntra_StopsTheApiFromStarting()
    {
        using var factory =
            baseFactory.WithWebHostBuilder(
                builder =>
                {
                    builder.UseSetting("ExternalId:TenantId", ExternalIdTestTokens.TenantId);
                    builder.UseSetting("ExternalId:TenantSubdomain", ExternalIdTestTokens.TenantSubdomain);
                    builder.UseSetting("ExternalId:ApiClientId", ExternalIdTestTokens.ApiClientId);
                });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Entra:ApiClientId", error.Message);
    }

    [Fact]
    public void Metadata_ComesFromTheExternalTenant()
    {
        using var factory =
            baseFactory.WithWebHostBuilder(
                builder =>
                {
                    builder.UseSetting("Entra:ApiClientId", EntraTestTokens.ApiClientId);
                    builder.UseSetting("ExternalId:TenantId", ExternalIdTestTokens.TenantId);
                    builder.UseSetting("ExternalId:TenantSubdomain", ExternalIdTestTokens.TenantSubdomain);
                    builder.UseSetting("ExternalId:ApiClientId", ExternalIdTestTokens.ApiClientId);
                });

        var jwt =
            factory.Services
                .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(ExternalIdAuthentication.SchemeName);

        Assert.Equal(
            $"https://roombookingtest.ciamlogin.com/{ExternalIdTestTokens.TenantId}/v2.0",
            jwt.Authority);
        Assert.True(jwt.RequireHttpsMetadata);
        Assert.Contains(ExternalIdTestTokens.ApiClientId, jwt.TokenValidationParameters.ValidAudiences);
        Assert.DoesNotContain(EntraTestTokens.ApiClientId, jwt.TokenValidationParameters.ValidAudiences);
    }

    [Theory]
    [InlineData("https://e1e1e1e1-2222-4333-8444-555555555555.ciamlogin.com/e1e1e1e1-2222-4333-8444-555555555555/v2.0", true)]
    [InlineData("https://roombookingtest.ciamlogin.com/e1e1e1e1-2222-4333-8444-555555555555/v2.0", true)]
    [InlineData("http://roombookingtest.ciamlogin.com/x/v2.0", false)]
    [InlineData("https://login.microsoftonline.com/e1e1e1e1-2222-4333-8444-555555555555/v2.0", false)]
    [InlineData("https://ciamlogin.com.evil.example/x/v2.0", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void ExternalIdIssuers_AreRecognisedByTheirHost(
        string? issuer,
        bool expected)
    {
        Assert.Equal(expected, ExternalIdAuthentication.IsExternalIdIssuer(issuer));
    }
}
