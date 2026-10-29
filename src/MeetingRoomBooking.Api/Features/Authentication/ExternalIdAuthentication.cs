using System.Security.Claims;
using System.Text.RegularExpressions;
using MeetingRoomBooking.Api.Features.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MeetingRoomBooking.Api.Features.Authentication;

/// <summary>
/// Validates access tokens from one Microsoft Entra External ID external
/// tenant, where people sign in with an e-mail address and a one-time
/// passcode. A separate JWT bearer scheme from Entra ID's: its own metadata
/// (signing keys and issuer), its own audience, and its tenant only.
/// </summary>
/// <remarks>
/// Microsoft's guidance for an API that trusts two token issuers is one
/// scheme per issuer, each validating signature, issuer and audience against
/// its own metadata. <see cref="IsExternalIdToken"/> only chooses which
/// scheme looks at a token; it never accepts one.
/// </remarks>
public static partial class ExternalIdAuthentication
{
    public const string SchemeName =
        "ExternalId";

    /// <summary>
    /// The authentication type of identities this scheme validated, so
    /// <see cref="SignedInIdentity"/> knows which issuer vouched for them.
    /// </summary>
    public const string AuthenticationType =
        "ExternalId";

    /// <summary>
    /// Whether the bearer token in the request names an External ID issuer
    /// (<c>https://{tenant}.ciamlogin.com/…</c>). The token is not validated
    /// here; the chosen scheme validates it completely.
    /// </summary>
    public static bool IsExternalIdToken(
        HttpRequest request)
    {
        string? header =
            request.Headers.Authorization;

        const string prefix = "Bearer ";

        if (header is null ||
            !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token =
            header[prefix.Length..].Trim();

        var handler =
            new JsonWebTokenHandler();

        if (!handler.CanReadToken(token))
        {
            return false;
        }

        try
        {
            return IsExternalIdIssuer(new JsonWebToken(token).Issuer);
        }
        catch (ArgumentException)
        {
            // Malformed: the Entra scheme rejects it.
            return false;
        }
    }

    public static bool IsExternalIdIssuer(
        string? issuer)
    {
        return Uri.TryCreate(issuer, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host.EndsWith(".ciamlogin.com", StringComparison.OrdinalIgnoreCase);
    }

    public static void AddExternalIdBearer(
        this AuthenticationBuilder authentication,
        ExternalIdAuthenticationOptions options)
    {
        Validate(options);

        var tenantId =
            Guid.Parse(options.TenantId!);

        authentication.AddJwtBearer(
            SchemeName,
            jwt =>
            {
                // Metadata of the one external tenant. The issuer to accept
                // is the one this metadata names
                // (https://{tenant ID}.ciamlogin.com/{tenant ID}/v2.0), as
                // Microsoft recommends, rather than a hand-built string.
                jwt.Authority = options.Authority;

                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters =
                    new TokenValidationParameters
                    {
                        ValidateAudience = true,
                        ValidAudiences =
                        [
                            options.ApiClientId!,
                            options.ApplicationIdUri
                            ?? $"api://{options.ApiClientId}"
                        ],
                        ValidateIssuer = true,
                        ValidateIssuerSigningKey = true,
                        RequireSignedTokens = true,
                        ValidAlgorithms =
                        [
                            SecurityAlgorithms.RsaSha256
                        ],
                        ValidateLifetime = true,
                        RequireExpirationTime = true,
                        ClockSkew = TimeSpan.FromMinutes(2),
                        NameClaimType = EntraAuthentication.Claims.Name,
                        AuthenticationType = AuthenticationType
                    };

                jwt.Events =
                    new JwtBearerEvents
                    {
                        OnTokenValidated =
                            context =>
                            {
                                OnTokenValidated(context, tenantId);
                                return Task.CompletedTask;
                            }
                    };
            });
    }

    private static void OnTokenValidated(
        TokenValidatedContext context,
        Guid tenantId)
    {
        var principal =
            context.Principal!;

        // The issuer already names the tenant; tid must agree, because the
        // identity key and the API's response use it.
        if (!Guid.TryParseExact(
                principal.FindFirstValue(EntraAuthentication.Claims.TenantId),
                "D",
                out var tokenTenant) ||
            tokenTenant != tenantId)
        {
            context.Fail("The token's tid is not the configured external tenant.");
        }
    }

    internal static void Validate(
        ExternalIdAuthenticationOptions options)
    {
        if (!Guid.TryParse(options.ApiClientId, out _))
        {
            throw new InvalidOperationException(
                "ExternalId:ApiClientId must be the Application (client) ID of the API's registration in the external tenant.");
        }

        if (!Guid.TryParseExact(options.TenantId, "D", out _))
        {
            throw new InvalidOperationException(
                "ExternalId:TenantId must be the external tenant's ID (a GUID).");
        }

        if (options.TenantSubdomain is null ||
            !SubdomainPattern().IsMatch(options.TenantSubdomain))
        {
            throw new InvalidOperationException(
                "ExternalId:TenantSubdomain must be the tenant's subdomain, e.g. contoso for contoso.onmicrosoft.com.");
        }
    }

    // One DNS label: letters, digits and inner hyphens.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubdomainPattern();
}
