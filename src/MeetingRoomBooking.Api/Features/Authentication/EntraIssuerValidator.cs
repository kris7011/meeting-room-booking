using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MeetingRoomBooking.Api.Features.Authentication;

/// <summary>
/// Issuer validation for a multi-tenant API. The metadata of the "common"
/// and "organizations" endpoints names the issuer as a template
/// (<c>https://login.microsoftonline.com/{tenantid}/v2.0</c>), so the
/// default exact-match check cannot be used. A token is accepted only if its
/// <c>iss</c> is exactly that template filled in with the token's own
/// <c>tid</c>, and its tenant is allowed: the consumer tenant only when
/// personal accounts are allowed, any other tenant only when no tenants are
/// configured or it is one of them.
/// </summary>
/// <remarks>
/// v1.0 tokens (issuer <c>https://sts.windows.net/{tid}/</c>) are rejected:
/// the stored <c>UserIdentities.Issuer</c> is the v2.0 issuer, so accepting
/// both would give one person two identities.
/// </remarks>
public sealed class EntraIssuerValidator
{
    private readonly string instance;
    private readonly HashSet<Guid> allowedTenantIds;
    private readonly bool allowPersonalAccounts;

    public EntraIssuerValidator(
        string instance,
        IEnumerable<Guid> allowedTenantIds,
        bool allowPersonalAccounts)
    {
        this.instance =
            instance.TrimEnd('/');

        this.allowedTenantIds =
            [.. allowedTenantIds];

        this.allowPersonalAccounts =
            allowPersonalAccounts;
    }

    public string Validate(
        string issuer,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        var tenantClaim =
            securityToken is JsonWebToken jsonWebToken &&
            jsonWebToken.TryGetPayloadValue<string>("tid", out var value)
                ? value
                : null;

        if (!Guid.TryParseExact(tenantClaim, "D", out var tenantId) ||
            tenantClaim != tenantId.ToString("D"))
        {
            throw new SecurityTokenInvalidIssuerException(
                "The token has no valid tid claim.");
        }

        var expectedIssuer =
            $"{instance}/{tenantClaim}/v2.0";

        if (!string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
        {
            throw new SecurityTokenInvalidIssuerException(
                "The token's issuer does not match its tenant.");
        }

        if (tenantId == EntraAuthentication.PersonalAccountsTenant)
        {
            if (!allowPersonalAccounts)
            {
                throw new SecurityTokenInvalidIssuerException(
                    "Personal Microsoft accounts are not allowed.");
            }
        }
        else if (allowedTenantIds.Count > 0 &&
                 !allowedTenantIds.Contains(tenantId))
        {
            throw new SecurityTokenInvalidIssuerException(
                "The token's tenant is not allowed.");
        }

        return issuer;
    }
}
