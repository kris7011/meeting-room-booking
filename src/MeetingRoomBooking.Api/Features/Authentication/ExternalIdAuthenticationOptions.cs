namespace MeetingRoomBooking.Api.Features.Authentication;

/// <summary>
/// Settings for validating access tokens from a Microsoft Entra External ID
/// external tenant (e-mail and one-time passcode sign-in), from the
/// <c>ExternalId</c> configuration section. Nothing here is a secret: the
/// API only validates tokens.
/// </summary>
/// <remarks>
/// Optional: without <see cref="ApiClientId"/> the API accepts Microsoft
/// Entra ID tokens only, exactly as before. External ID is an additional way
/// to sign in, so it requires the <c>Entra</c> section to be configured too.
/// There is deliberately no setting that makes the token's <c>email</c>
/// claim count as verified: External ID users never have a verified e-mail
/// (see <see cref="Users.SignedInIdentity"/> and docs/external-id-setup.md).
/// </remarks>
public sealed class ExternalIdAuthenticationOptions
{
    public const string SectionName = "ExternalId";

    /// <summary>The external tenant's ID (a GUID), shown on its overview page.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The tenant's subdomain: <c>contoso</c> for the primary domain
    /// <c>contoso.onmicrosoft.com</c>. Sign-in runs on
    /// <c>https://{subdomain}.ciamlogin.com</c>.
    /// </summary>
    public string? TenantSubdomain { get; set; }

    /// <summary>
    /// Application (client) ID of the API's app registration in the external
    /// tenant. That registration is separate from the Entra ID one, so its
    /// tokens have a different audience.
    /// </summary>
    public string? ApiClientId { get; set; }

    /// <summary>The API's Application ID URI there. Defaults to <c>api://{ApiClientId}</c>.</summary>
    public string? ApplicationIdUri { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiClientId);

    /// <summary>
    /// Where the API reads the tenant's OpenID Connect metadata (signing keys
    /// and issuer): <c>https://{subdomain}.ciamlogin.com/{tenant ID}/v2.0</c>.
    /// </summary>
    public string Authority =>
        $"https://{TenantSubdomain}.ciamlogin.com/{TenantId}/v2.0";
}
