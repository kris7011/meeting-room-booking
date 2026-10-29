namespace MeetingRoomBooking.Api.Features.Users;

/// <summary>Who issued a sign-in identity.</summary>
public enum IdentityProvider
{
    /// <summary>Microsoft 365 through Microsoft Entra ID (multi-tenant).</summary>
    Entra,

    /// <summary>E-mail and one-time code through Microsoft Entra External ID.</summary>
    ExternalId
}

/// <summary>
/// One way a user signs in. A user can have several (for example Microsoft
/// 365 and e-mail), but an identity belongs to exactly one user.
/// </summary>
/// <remarks>
/// Rows are only ever created from a token whose signature and issuer the
/// API has validated. Two identities are never linked to the same user
/// because their e-mail addresses match; linking requires proof of both.
/// </remarks>
public sealed class UserIdentity
{
    internal const int MaximumIssuerLength = 512;
    internal const int MaximumSubjectLength = 255;
    internal const int MaximumEmailLength = 254;

    private UserIdentity()
    {
    }

    public int Id { get; private set; }

    public int UserId { get; private set; }

    public IdentityProvider Provider { get; private set; }

    /// <summary>
    /// The validated "iss" claim exactly as issued. For Microsoft Entra ID it
    /// contains the user's tenant ID, so together with <see cref="Subject"/>
    /// it identifies the user across tenants.
    /// </summary>
    public string Issuer { get; private set; } =
        string.Empty;

    /// <summary>
    /// The issuer's immutable user ID: the "oid" claim for both Microsoft
    /// issuers. ("sub" differs between the app's ID token and the API's
    /// access token, so it cannot be the key.)
    /// </summary>
    public string Subject { get; private set; } =
        string.Empty;

    /// <summary>
    /// The e-mail address the issuer has verified, in lower case, or null.
    /// Used only to match invitations; it is not unique and never a key.
    /// </summary>
    public string? VerifiedEmail { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? LastUsedUtc { get; private set; }

    /// <summary>
    /// An identity of <paramref name="provider"/> for <paramref name="userId"/>,
    /// keyed by the validated issuer and object ID.
    /// </summary>
    public static UserIdentity Create(
        int userId,
        IdentityProvider provider,
        string issuer,
        string objectId,
        string? verifiedEmail,
        DateTimeOffset nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        if (issuer.Length > MaximumIssuerLength ||
            objectId.Length > MaximumSubjectLength)
        {
            throw new ArgumentException(
                "The issuer or object ID is longer than an identity can store.");
        }

        return new UserIdentity
        {
            UserId = userId,
            Provider = provider,
            Issuer = issuer,
            Subject = objectId,
            VerifiedEmail = verifiedEmail,
            CreatedUtc = nowUtc.ToUniversalTime(),
            LastUsedUtc = nowUtc.ToUniversalTime()
        };
    }

    /// <summary>
    /// Records a sign-in. The verified e-mail follows the issuer: an address
    /// the latest token no longer vouches for is dropped.
    /// </summary>
    public void RecordUse(
        string? verifiedEmail,
        DateTimeOffset nowUtc)
    {
        VerifiedEmail = verifiedEmail;
        LastUsedUtc = nowUtc.ToUniversalTime();
    }
}
