namespace MeetingRoomBooking.Api.Features.Organizations;

public enum MembershipRole
{
    Owner,
    Admin,
    Member
}

/// <summary>
/// A user's role in one organisation. A user can belong to several
/// organisations, but has at most one membership in each.
/// </summary>
/// <remarks>
/// Nobody becomes a member automatically, of the legacy organisation or any
/// other. Once an organisation has an Owner, the database refuses to delete
/// or demote its last Owner (triggers TR_Memberships_LastOwner_*).
/// </remarks>
public sealed class Membership
{
    private Membership()
    {
    }

    public int OrganizationId { get; private set; }

    public int UserId { get; private set; }

    public MembershipRole Role { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public int? CreatedByUserId { get; private set; }

    public DateTimeOffset? UpdatedUtc { get; private set; }
}
