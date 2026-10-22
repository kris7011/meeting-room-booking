using System.Security.Cryptography;
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
/// Signs access tokens the way Microsoft Entra ID does, with a key that
/// exists only in the test process, and points the API's JWT validation at
/// that key instead of Entra's metadata. Everything else (issuer, audience,
/// scope, lifetime and signing-key-issuer checks) is the production code.
/// No real tenant, app or token is involved.
/// </summary>
public static class EntraTestTokens
{
    public const string ApiClientId =
        "c0ffee00-1111-4222-8333-444455556666";

    public const string TenantId =
        "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";

    public const string OtherTenantId =
        "ffffffff-0000-4111-8222-333333333333";

    public const string ObjectId =
        "12345678-90ab-4cde-8f01-234567890abc";

    public const string KeyId =
        "test-signing-key";

    public static string IssuerFor(string tenantId) =>
        $"https://login.microsoftonline.com/{tenantId}/v2.0";

    /// <summary>Entra's issuer template, as published on signing keys in the metadata.</summary>
    public const string IssuerTemplate =
        "https://login.microsoftonline.com/{tenantid}/v2.0";

    private static readonly RsaSecurityKey SigningKey =
        CreateKey(KeyId);

    /// <summary>A key nobody trusts: tokens signed with it must be rejected.</summary>
    private static readonly RsaSecurityKey UntrustedKey =
        CreateKey(KeyId);

    public static Dictionary<string, object> UserClaims(
        string tenantId = TenantId)
    {
        return new Dictionary<string, object>
        {
            ["iss"] = IssuerFor(tenantId),
            ["aud"] = ApiClientId,
            ["tid"] = tenantId,
            ["oid"] = ObjectId,
            ["sub"] = "api-pairwise-subject",
            ["scp"] = "access_as_user",
            ["name"] = "Søren Ærø",
            ["preferred_username"] = "soren@contoso.example",
            ["azp"] = "11111111-2222-4333-8444-555555555555",
            ["ver"] = "2.0"
        };
    }

    public static string Create(
        Dictionary<string, object> claims,
        DateTime? expires = null,
        bool untrustedKey = false,
        bool symmetricKey = false)
    {
        var expiry = expires ?? DateTime.UtcNow.AddHours(1);

        // HS256 with a shared secret: Entra never signs access tokens this
        // way, and the API only accepts RS256.
        var credentials =
            symmetricKey
                ? new SigningCredentials(
                    new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = KeyId },
                    SecurityAlgorithms.HmacSha256)
                : new SigningCredentials(
                    untrustedKey ? UntrustedKey : SigningKey,
                    SecurityAlgorithms.RsaSha256);

        var descriptor =
            new SecurityTokenDescriptor
            {
                Claims = claims,
                IssuedAt = expiry.AddHours(-1),
                NotBefore = expiry.AddHours(-1),
                Expires = expiry,
                SigningCredentials = credentials
            };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>An unsigned token ("alg": "none").</summary>
    public static string CreateUnsigned(
        Dictionary<string, object> claims)
    {
        var now = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Claims = claims,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddHours(1)
            });
    }

    /// <summary>
    /// Configures the API for Entra with the test key. <paramref name="keyIssuer"/>
    /// is the issuer the metadata names for the key, as Entra's JWKS does.
    /// </summary>
    public static IWebHostBuilder UseEntraTestTokens(
        this IWebHostBuilder builder,
        string keyIssuer = IssuerTemplate,
        params string[] allowedTenantIds)
    {
        builder.UseSetting("Entra:ApiClientId", ApiClientId);

        for (var index = 0; index < allowedTenantIds.Length; index++)
        {
            builder.UseSetting($"Entra:AllowedTenantIds:{index}", allowedTenantIds[index]);
        }

        return builder.ConfigureTestServices(
            services =>
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        var publicKey =
                            JsonWebKeyConverter.ConvertFromRSASecurityKey(
                                new RsaSecurityKey(SigningKey.Rsa.ExportParameters(false))
                                {
                                    KeyId = KeyId
                                });

                        publicKey.AdditionalData["issuer"] = keyIssuer;

                        var configuration =
                            new OpenIdConnectConfiguration
                            {
                                Issuer = IssuerTemplate,
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

    private static RsaSecurityKey CreateKey(
        string keyId)
    {
        return new RsaSecurityKey(RSA.Create(2048))
        {
            KeyId = keyId
        };
    }
}
