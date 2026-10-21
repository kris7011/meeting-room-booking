namespace MeetingRoomBooking.Api.Features.Organizations;

/// <summary>
/// A customer's workspace. Every meeting room, and through it every booking,
/// belongs to exactly one organisation.
/// </summary>
public sealed class Organization
{
    internal const int MinimumNameLength = 2;
    internal const int MaximumNameLength = 100;

    /// <summary>Length of a hex-encoded SHA-256 hash.</summary>
    internal const int CreationRequestHashLength = 64;

    private Organization()
    {
    }

    public int Id { get; private set; }

    public Guid PublicId { get; private set; }

    public string Name { get; private set; } =
        string.Empty;

    public bool IsLegacy { get; private set; }

    public int? CreatedByUserId { get; private set; }

    /// <summary>
    /// The creator's Idempotency-Key. A repeated request with the same key
    /// returns this organisation instead of creating another.
    /// </summary>
    public Guid? CreationRequestId { get; private set; }

    /// <summary>
    /// Hash of the request the key was first used with, so the same key with
    /// a different request can be rejected.
    /// </summary>
    public string? CreationRequestHash { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? DeletedUtc { get; private set; }
}
