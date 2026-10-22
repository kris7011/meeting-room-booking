namespace MeetingRoomBooking.Api.Features.Authentication;

/// <summary>
/// Settings for validating Microsoft Entra ID access tokens, from the
/// <c>Entra</c> configuration section. Nothing here is a secret: the API
/// only validates tokens and never requests one, so it has no client secret.
/// </summary>
/// <remarks>
/// Without <see cref="ApiClientId"/> the API starts without sign-in: the
/// existing endpoints work as before and <c>GET /api/me</c> answers 503.
/// </remarks>
public sealed class EntraAuthenticationOptions
{
    public const string SectionName = "Entra";

    /// <summary>The Microsoft identity platform host for the public cloud.</summary>
    public string Instance { get; set; } =
        "https://login.microsoftonline.com";

    /// <summary>
    /// Application (client) ID of the API's app registration. v2.0 access
    /// tokens for the API carry it as their audience.
    /// </summary>
    public string? ApiClientId { get; set; }

    /// <summary>
    /// The API's Application ID URI, accepted as an audience as well.
    /// Defaults to <c>api://{ApiClientId}</c>.
    /// </summary>
    public string? ApplicationIdUri { get; set; }

    /// <summary>The delegated scope every signed-in call must carry in <c>scp</c>.</summary>
    public string RequiredScope { get; set; } =
        "access_as_user";

    /// <summary>
    /// Tenants whose users are accepted. Empty accepts work and school
    /// accounts of any tenant (a multi-tenant API); access to data is then
    /// decided by memberships, not by the tenant.
    /// </summary>
    public string[] AllowedTenantIds { get; set; } =
        [];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiClientId);
}
