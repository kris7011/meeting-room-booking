using System.Text.Json;

namespace MeetingRoomBooking.Api.Features.Audit;

/// <summary>
/// What happened. Stored as text, so new kinds can be added without a
/// database change.
/// </summary>
public enum AuditEventType
{
    OrganizationCreated,
    OrganizationRenamed,
    OrganizationDeleted,
    MembershipAdded,
    MembershipRoleChanged,
    MembershipRemoved,
    InvitationCreated,
    InvitationRevoked,
    InvitationAccepted,
    UserRegistered,
    RoomCreated,
    RoomUpdated,
    RoomArchived,
    RoomRestored
}

/// <summary>
/// A record of a change to an organisation, its memberships, roles or
/// invitations: who did what, where and when.
/// </summary>
/// <remarks>
/// <see cref="Details"/> must never contain access tokens, invitation tokens
/// or token hashes; the service layer that writes events enforces this.
/// </remarks>
public sealed class AuditEvent
{
    internal const int MaximumTypeLength = 64;

    private AuditEvent()
    {
    }

    public long Id { get; private set; }

    /// <summary>Null only for events that concern no organisation.</summary>
    public int? OrganizationId { get; private set; }

    /// <summary>The signed-in user who made the change; null for the system.</summary>
    public int? ActorUserId { get; private set; }

    /// <summary>The user the change was about, for membership events.</summary>
    public int? TargetUserId { get; private set; }

    public AuditEventType Type { get; private set; }

    public DateTimeOffset OccurredUtc { get; private set; }

    /// <summary>Optional JSON with non-secret details, such as old and new role.</summary>
    public string? Details { get; private set; }

    /// <param name="details">
    /// Non-secret details only: never an access token, an invitation token
    /// or a token hash.
    /// </param>
    public static AuditEvent Record(
        AuditEventType type,
        int? organizationId,
        int? actorUserId,
        int? targetUserId,
        DateTimeOffset nowUtc,
        object? details = null)
    {
        return new AuditEvent
        {
            Type = type,
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            OccurredUtc = nowUtc.ToUniversalTime(),
            Details =
                details is null
                    ? null
                    : JsonSerializer.Serialize(details, DetailsJsonOptions)
        };
    }

    private static readonly JsonSerializerOptions DetailsJsonOptions =
        new(JsonSerializerDefaults.Web);
}
