namespace MeetingRoomBooking.Api.Features.Users;

/// <summary>
/// One attempt to add a second sign-in identity to an existing user. It
/// needs proof of both identities and the user's confirmation, in this
/// order:
/// <list type="number">
///   <item>Started by the user, signed in with an identity they already
///   have: the API returns a one-time code, which only that session sees.</item>
///   <item>Claimed with the code by the new identity, signed in on its own
///   (proof that the same person controls it).</item>
///   <item>Confirmed by the user, signed in with an existing identity
///   again, after seeing both.</item>
/// </list>
/// Nothing is linked before the confirmation, and never because two
/// identities share an e-mail address.
/// </summary>
/// <remarks>
/// The database keeps only the code's SHA-256 hash, so a copy of it cannot
/// be used to claim a request.
/// </remarks>
public sealed class IdentityLinkRequest
{
    /// <summary>How long a started request can be claimed and confirmed.</summary>
    public static readonly TimeSpan Lifetime =
        TimeSpan.FromMinutes(10);

    private IdentityLinkRequest()
    {
    }

    public int Id { get; private set; }

    /// <summary>What clients use to refer to the request.</summary>
    public Guid PublicId { get; private set; }

    /// <summary>The user the new identity will belong to: the one who started it.</summary>
    public int UserId { get; private set; }

    public string CodeHash { get; private set; } =
        string.Empty;

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset ExpiresUtc { get; private set; }

    /// <summary>The identity that claimed the code; all four are set together.</summary>
    public IdentityProvider? ClaimedProvider { get; private set; }

    public string? ClaimedIssuer { get; private set; }

    public string? ClaimedSubject { get; private set; }

    public DateTimeOffset? ClaimedUtc { get; private set; }

    /// <summary>The claimed identity's verified e-mail, kept for its <see cref="UserIdentity"/> row.</summary>
    public string? ClaimedVerifiedEmail { get; private set; }

    public DateTimeOffset? CompletedUtc { get; private set; }

    public DateTimeOffset? CancelledUtc { get; private set; }

    public bool IsClaimed =>
        ClaimedUtc is not null;

    public bool IsOpen =>
        CompletedUtc is null && CancelledUtc is null;

    public bool IsExpired(
        DateTimeOffset nowUtc) =>
        nowUtc >= ExpiresUtc;

    /// <param name="codeHash">From <see cref="IdentityLinkCode.Create"/>; never the code itself.</param>
    public static IdentityLinkRequest Start(
        int userId,
        string codeHash,
        DateTimeOffset nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        if (codeHash.Length != IdentityLinkCode.HashLength)
        {
            throw new ArgumentException("Expected the code's SHA-256 hash.", nameof(codeHash));
        }

        return new IdentityLinkRequest
        {
            PublicId = Guid.NewGuid(),
            UserId = userId,
            CodeHash = codeHash,
            CreatedUtc = nowUtc.ToUniversalTime(),
            ExpiresUtc = nowUtc.ToUniversalTime() + Lifetime
        };
    }

    /// <summary>Whether <paramref name="identity"/> is the identity that claimed this request.</summary>
    public bool WasClaimedBy(
        SignedInIdentity identity) =>
        IsClaimed &&
        ClaimedProvider == identity.Provider &&
        ClaimedIssuer == identity.Issuer &&
        ClaimedSubject == identity.ObjectId;

    public void Claim(
        SignedInIdentity identity,
        DateTimeOffset nowUtc)
    {
        if (!IsOpen || IsClaimed)
        {
            throw new InvalidOperationException("Only an open, unclaimed request can be claimed.");
        }

        ClaimedProvider = identity.Provider;
        ClaimedIssuer = identity.Issuer;
        ClaimedSubject = identity.ObjectId;
        ClaimedVerifiedEmail = identity.VerifiedEmail;
        ClaimedUtc = nowUtc.ToUniversalTime();
    }

    public void Complete(
        DateTimeOffset nowUtc)
    {
        if (!IsOpen || !IsClaimed)
        {
            throw new InvalidOperationException("Only an open, claimed request can be completed.");
        }

        CompletedUtc = nowUtc.ToUniversalTime();
    }

    public void Cancel(
        DateTimeOffset nowUtc)
    {
        if (IsOpen)
        {
            CancelledUtc = nowUtc.ToUniversalTime();
        }
    }
}

/// <summary>
/// The one-time code of a link request: the same format as an invitation
/// token (32 random bytes, URL-safe; only its SHA-256 hash is stored).
/// </summary>
public static class IdentityLinkCode
{
    public const int HashLength =
        Organizations.InvitationToken.HashLength;

    /// <summary>Longer input is not a code and is not hashed.</summary>
    public const int MaximumLength =
        128;

    public static (string Code, string Hash) Create()
    {
        var (token, hash) = Organizations.InvitationToken.Create();
        return (token, hash);
    }

    public static string Hash(
        string code) =>
        Organizations.InvitationToken.Hash(code);
}
