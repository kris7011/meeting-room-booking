namespace MeetingRoomBooking.Api.Features.Organizations;

/// <summary>
/// An invitation to join one organisation with a given role, addressed to
/// one e-mail address. Only a SHA-256 hash of the invitation token is stored;
/// the token itself is shown once, in the invitation link.
/// </summary>
/// <remarks>
/// Accepting will require a signed-in user whose identity has a verified
/// e-mail matching <see cref="Email"/>. An invitation can never grant the
/// Owner role; ownership is transferred between members.
/// </remarks>
public sealed class Invitation
{
    internal const int MaximumEmailLength = 254;

    private Invitation()
    {
    }

    public int Id { get; private set; }

    public int OrganizationId { get; private set; }

    /// <summary>Trimmed and in lower case.</summary>
    public string Email { get; private set; } =
        string.Empty;

    /// <summary>Admin or Member.</summary>
    public MembershipRole Role { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the token; see <see cref="InvitationToken"/>.</summary>
    public string TokenHash { get; private set; } =
        string.Empty;

    public int InvitedByUserId { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset ExpiresUtc { get; private set; }

    public int? AcceptedByUserId { get; private set; }

    public DateTimeOffset? AcceptedUtc { get; private set; }

    public DateTimeOffset? RevokedUtc { get; private set; }

    /// <summary>Neither accepted nor revoked. An open invitation can still have expired.</summary>
    public bool IsOpen =>
        AcceptedUtc is null && RevokedUtc is null;

    public bool IsExpired(
        DateTimeOffset nowUtc) =>
        nowUtc >= ExpiresUtc;

    /// <param name="email">Already trimmed and in lower case.</param>
    /// <param name="tokenHash">From <see cref="InvitationToken.Create"/>; never the token itself.</param>
    public static Invitation Create(
        int organizationId,
        string email,
        MembershipRole role,
        string tokenHash,
        int invitedByUserId,
        DateTimeOffset nowUtc,
        TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(organizationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(invitedByUserId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        if (role == MembershipRole.Owner)
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                "An invitation can never grant the Owner role.");
        }

        if (tokenHash.Length != InvitationToken.HashLength)
        {
            throw new ArgumentException(
                "Only the token's SHA-256 hash is stored.",
                nameof(tokenHash));
        }

        return new Invitation
        {
            OrganizationId = organizationId,
            Email = email,
            Role = role,
            TokenHash = tokenHash,
            InvitedByUserId = invitedByUserId,
            CreatedUtc = nowUtc.ToUniversalTime(),
            ExpiresUtc = nowUtc.ToUniversalTime() + lifetime
        };
    }

    public void Accept(
        int userId,
        DateTimeOffset nowUtc)
    {
        AcceptedByUserId = userId;
        AcceptedUtc = nowUtc.ToUniversalTime();
    }

    public void Revoke(
        DateTimeOffset nowUtc)
    {
        RevokedUtc = nowUtc.ToUniversalTime();
    }
}
