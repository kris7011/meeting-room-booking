using System.Security.Claims;
using MeetingRoomBooking.Api.Features.Authentication;

namespace MeetingRoomBooking.Api.Features.Users;

/// <summary>
/// The caller, read from an access token the API has validated (signature,
/// issuer, audience, lifetime and scope). Nothing here comes from the
/// request body or anything else the client says about itself.
/// </summary>
/// <param name="Issuer">The validated <c>iss</c>; with <paramref name="ObjectId"/> the identity key.</param>
/// <param name="ObjectId">The immutable <c>oid</c>.</param>
/// <param name="Username"><c>preferred_username</c>: display only, never a key and never verified.</param>
/// <param name="VerifiedEmail">
/// An address Microsoft vouches for, in lower case, or null. See <see cref="ReadVerifiedEmail"/>.
/// </param>
public sealed record SignedInIdentity(
    string Issuer,
    string ObjectId,
    string TenantId,
    string DisplayName,
    string? Username,
    string? VerifiedEmail)
{
    /// <summary>Optional claim: whether the domain owner of <c>email</c> has been verified.</summary>
    public const string EmailDomainOwnerVerifiedClaim = "xms_edov";

    public const string EmailClaim = "email";

    public static SignedInIdentity From(
        ClaimsPrincipal principal)
    {
        var objectId =
            Required(principal, EntraAuthentication.Claims.ObjectId);

        var username =
            principal.FindFirstValue(EntraAuthentication.Claims.PreferredUsername);

        var name =
            principal.FindFirstValue(EntraAuthentication.Claims.Name);

        return new SignedInIdentity(
            Required(principal, EntraAuthentication.Claims.Issuer),
            objectId,
            Required(principal, EntraAuthentication.Claims.TenantId),
            !string.IsNullOrWhiteSpace(name)
                ? name
                : !string.IsNullOrWhiteSpace(username)
                    ? username
                    : objectId,
            username,
            ReadVerifiedEmail(principal));
    }

    /// <summary>
    /// The <c>email</c> claim, but only when the token also says
    /// <c>xms_edov: true</c>. Microsoft documents <c>email</c> on its own as
    /// unverified and mutable, and <c>preferred_username</c> is never an
    /// e-mail the API can rely on. Both are optional claims of the API's app
    /// registration; without them every identity has no verified e-mail.
    /// </summary>
    /// <remarks>
    /// A verified e-mail never identifies a user and never grants access on
    /// its own; it is only compared with the address an invitation was sent
    /// to, together with the invitation's secret token.
    /// </remarks>
    public static string? ReadVerifiedEmail(
        ClaimsPrincipal principal)
    {
        var verified =
            principal.FindFirstValue(EmailDomainOwnerVerifiedClaim);

        if (!string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase) &&
            verified != "1")
        {
            return null;
        }

        return EmailAddress.Normalize(
            principal.FindFirstValue(EmailClaim));
    }

    // The policy guarantees tid and oid; iss is always present on a
    // validated token.
    private static string Required(
        ClaimsPrincipal principal,
        string claimType)
    {
        return principal.FindFirstValue(claimType)
            ?? throw new InvalidOperationException(
                $"The validated token has no {claimType} claim.");
    }
}

/// <summary>The one place e-mail addresses are normalised for comparison.</summary>
public static class EmailAddress
{
    public const int MaximumLength = 254;

    /// <summary>Trimmed and in lower case, or null if it is not shaped like an address.</summary>
    public static string? Normalize(
        string? value)
    {
        var email =
            value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(email) ||
            email.Length > MaximumLength ||
            email.IndexOf('@') < 1 ||
            email.IndexOf('@') != email.LastIndexOf('@') ||
            email.EndsWith('@') ||
            email.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return email;
    }
}
