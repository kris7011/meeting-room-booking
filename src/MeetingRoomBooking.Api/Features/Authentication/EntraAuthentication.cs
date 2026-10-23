using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;

namespace MeetingRoomBooking.Api.Features.Authentication;

/// <summary>
/// Validates Microsoft Entra ID v2.0 access tokens for this API. The API
/// alone decides who the caller is: from a token whose signature, issuer,
/// audience, lifetime and scope it has checked, never from anything the
/// client says about itself.
/// </summary>
public static class EntraAuthentication
{
    /// <summary>Signed-in users with a delegated token for this API.</summary>
    public const string SignedInUserPolicy =
        "EntraSignedInUser";

    /// <summary>
    /// The tenant that personal Microsoft accounts (Outlook.com, Hotmail,
    /// Live.com) sign in through: their tokens carry it as <c>tid</c> and in
    /// <c>iss</c>.
    /// </summary>
    public const string PersonalAccountsTenantId =
        "9188040d-6c67-4c5b-b112-36a304b66dad";

    public static readonly Guid PersonalAccountsTenant =
        Guid.Parse(PersonalAccountsTenantId);

    /// <summary>Claim names as they appear in the token (inbound mapping is off).</summary>
    public static class Claims
    {
        public const string Issuer = "iss";
        public const string TenantId = "tid";
        public const string ObjectId = "oid";
        public const string Scope = "scp";
        public const string Name = "name";
        public const string PreferredUsername = "preferred_username";
    }

    public static EntraAuthenticationOptions AddEntraAuthentication(
        this WebApplicationBuilder builder)
    {
        var options =
            builder.Configuration
                .GetSection(EntraAuthenticationOptions.SectionName)
                .Get<EntraAuthenticationOptions>()
            ?? new EntraAuthenticationOptions();

        builder.Services.AddSingleton(options);

        // Registered either way, so UseAuthentication/UseAuthorization work
        // and anonymous endpoints stay anonymous.
        var authentication =
            builder.Services.AddAuthentication(
                JwtBearerDefaults.AuthenticationScheme);

        builder.Services.AddAuthorization(
            authorization =>
                authorization.AddPolicy(
                    SignedInUserPolicy,
                    policy =>
                        policy
                            .AddAuthenticationSchemes(
                                JwtBearerDefaults.AuthenticationScheme)
                            .RequireAuthenticatedUser()
                            .RequireClaim(Claims.TenantId)
                            .RequireClaim(Claims.ObjectId)
                            // App-only tokens have roles but no scp, so they
                            // never pass as a user.
                            .RequireAssertion(
                                context =>
                                    HasScope(
                                        context.User,
                                        options.RequiredScope))));

        if (!options.IsConfigured)
        {
            return options;
        }

        Validate(options);

        var instance =
            options.Instance.TrimEnd('/');

        var issuerValidator =
            new EntraIssuerValidator(
                instance,
                options.AllowedTenantIds.Select(Guid.Parse),
                options.AllowPersonalAccounts);

        authentication.AddJwtBearer(
            jwt =>
            {
                // Signing keys come from the metadata of the "common"
                // endpoint: the keys shared by every tenant, plus the keys
                // only personal accounts are signed with. "organizations"
                // lacks the latter, so it is used only when personal accounts
                // are off. The issuer validator, not the key set, decides
                // which tenants are accepted.
                jwt.Authority =
                    options.AllowPersonalAccounts
                        ? $"{instance}/common/v2.0"
                        : $"{instance}/organizations/v2.0";

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
                        IssuerValidator = issuerValidator.Validate,
                        ValidateIssuerSigningKey = true,
                        RequireSignedTokens = true,
                        ValidAlgorithms =
                        [
                            SecurityAlgorithms.RsaSha256
                        ],
                        ValidateLifetime = true,
                        RequireExpirationTime = true,
                        ClockSkew = TimeSpan.FromMinutes(2),
                        NameClaimType = Claims.Name
                    };

                // Rejects a token signed with a key that Entra publishes for
                // another issuer (keys in the metadata can name their issuer).
                // The personal-account keys name the consumer tenant's
                // issuer, so they never validate a work or school token.
                jwt.TokenValidationParameters
                    .EnableAadSigningKeyIssuerValidation();
            });

        return options;
    }

    public static bool HasScope(
        ClaimsPrincipal user,
        string scope)
    {
        return user
            .FindAll(Claims.Scope)
            .SelectMany(
                claim =>
                    claim.Value.Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
    }

    private static void Validate(
        EntraAuthenticationOptions options)
    {
        if (!Guid.TryParse(options.ApiClientId, out _))
        {
            throw new InvalidOperationException(
                "Entra:ApiClientId must be the API app registration's Application (client) ID.");
        }

        if (!Uri.TryCreate(options.Instance, UriKind.Absolute, out var instance) ||
            instance.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Entra:Instance must be an absolute HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(options.RequiredScope) ||
            options.RequiredScope.Contains(' '))
        {
            throw new InvalidOperationException(
                "Entra:RequiredScope must be one scope name, e.g. access_as_user.");
        }

        foreach (var tenantId in options.AllowedTenantIds)
        {
            if (!Guid.TryParse(tenantId, out _))
            {
                throw new InvalidOperationException(
                    $"Entra:AllowedTenantIds contains '{tenantId}', which is not a tenant ID.");
            }

            if (Guid.Parse(tenantId) == PersonalAccountsTenant)
            {
                throw new InvalidOperationException(
                    "Entra:AllowedTenantIds lists work and school tenants only. " +
                    "Personal Microsoft accounts are allowed with Entra:AllowPersonalAccounts.");
            }
        }
    }
}
