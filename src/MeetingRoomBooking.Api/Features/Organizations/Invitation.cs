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
}
