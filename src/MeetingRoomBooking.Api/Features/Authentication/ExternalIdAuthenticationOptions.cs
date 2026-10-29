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

    /// <summary>
    /// Whether the token's <c>email</c> claim may count as a verified address
    /// (for accepting invitations). Off by default: Microsoft documents that
    /// local accounts prove their e-mail with a one-time passcode, but not
    /// that the <c>email</c> claim in the API's access token is that address.
    /// Turn it on only after checking a real token of this tenant, and only
    /// if the user flow offers e-mail accounts alone (no social or federated
    /// providers). See docs/external-id-setup.md.
    /// </summary>
    public bool EmailClaimIsVerified { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiClientId);

    /// <summary>
    /// Where the API reads the tenant's OpenID Connect metadata (signing keys
    /// and issuer): <c>https://{subdomain}.ciamlogin.com/{tenant ID}/v2.0</c>.
    /// </summary>
    public string Authority =>
        $"https://{TenantSubdomain}.ciamlogin.com/{TenantId}/v2.0";
}
