using System.Security.Cryptography;
using MeetingRoomBooking.Api.Features.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MeetingRoomBooking.Api.IntegrationTests.Authentication;

/// <summary>
/// Signs access tokens the way a Microsoft Entra External ID external tenant
/// does, with its own test key, and points the API's External ID scheme at
/// metadata naming that key and the tenant's issuer. Like
/// <see cref="EntraTestTokens"/>, no real tenant, app or token is involved;
/// the issuer format is the one Microsoft documents for external tenants.
/// </summary>
public static class ExternalIdTestTokens
{
    public const string TenantId =
        "e1e1e1e1-2222-4333-8444-555555555555";

    public const string OtherTenantId =
        "e2e2e2e2-2222-4333-8444-555555555555";

    public const string TenantSubdomain =
        "roombookingtest";

    /// <summary>The API's registration in the external tenant: not the Entra one.</summary>
    public const string ApiClientId =
        "c1c1c1c1-1111-4222-8333-444455556666";

    public const string ObjectId =
        "0b1d0b1d-90ab-4cde-8f01-234567890abc";

    public static string IssuerFor(string tenantId) =>
        $"https://{tenantId}.ciamlogin.com/{tenantId}/v2.0";

    public static readonly string Issuer =
        IssuerFor(TenantId);

    private static readonly RsaSecurityKey SigningKey =
        new(RSA.Create(2048))
        {
            KeyId = "external-id-test-key"
        };

    public static Dictionary<string, object> UserClaims(
        string objectId = ObjectId)
    {
        return new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = ApiClientId,
            ["tid"] = TenantId,
            ["oid"] = objectId,
            ["sub"] = "external-pairwise-subject",
            ["scp"] = "access_as_user",
            ["name"] = "Emma E-mail",
            ["azp"] = "a1a1a1a1-2222-4333-8444-555555555555",
            ["ver"] = "2.0"
        };
    }

    /// <summary>A delegated External ID token for one person, optionally with an <c>email</c> claim.</summary>
    public static string TokenFor(
        string objectId,
        string? email = null,
        string name = "Emma E-mail")
    {
        var claims = UserClaims(objectId);
        claims["name"] = name;

        if (email is not null)
        {
            claims["email"] = email;
        }

        return Create(claims);
    }

    public static string Create(
        Dictionary<string, object> claims,
        DateTime? expires = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddHours(1);

        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Claims = claims,
                IssuedAt = expiry.AddHours(-1),
                NotBefore = expiry.AddHours(-1),
                Expires = expiry,
                SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
            });
    }

    /// <summary>
    /// Configures Entra (with <see cref="EntraTestTokens"/>) and External ID
    /// with the test key. External ID requires the Entra settings.
    /// </summary>
    public static IWebHostBuilder UseExternalIdTestTokens(
        this IWebHostBuilder builder)
    {
        builder.UseEntraTestTokens();
        builder.UseSetting("ExternalId:TenantId", TenantId);
        builder.UseSetting("ExternalId:TenantSubdomain", TenantSubdomain);
        builder.UseSetting("ExternalId:ApiClientId", ApiClientId);

        return builder.ConfigureTestServices(
            services =>
                services.PostConfigure<JwtBearerOptions>(
                    ExternalIdAuthentication.SchemeName,
                    options =>
                    {
                        var publicKey =
                            JsonWebKeyConverter.ConvertFromRSASecurityKey(
                                new RsaSecurityKey(SigningKey.Rsa.ExportParameters(false))
                                {
                                    KeyId = SigningKey.KeyId
                                });

                        // As in the tenant's metadata document: one exact issuer.
                        var configuration =
                            new OpenIdConnectConfiguration
                            {
                                Issuer = Issuer,
                                JsonWebKeySet = new JsonWebKeySet()
                            };

                        configuration.JsonWebKeySet.Keys.Add(publicKey);
                        configuration.SigningKeys.Add(publicKey);

                        options.Configuration = configuration;
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(
                                configuration);
                    }));
    }
}
