using MeetingRoomBooking.Api.Features.Text;

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

    /// <summary>
    /// A new organisation created by a signed-in user, keyed by their
    /// Idempotency-Key. The creator becomes its first Owner in the same
    /// transaction (see <see cref="OrganizationService"/>).
    /// </summary>
    /// <param name="name">Already normalised with <see cref="NameText.Normalize"/>.</param>
    /// <param name="creationRequestHash">Lower-case hex SHA-256 of the normalised request.</param>
    public static Organization Create(
        string name,
        int createdByUserId,
        Guid creationRequestId,
        string creationRequestHash,
        DateTimeOffset nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(createdByUserId);
        ValidateName(name);

        if (creationRequestHash.Length != CreationRequestHashLength)
        {
            throw new ArgumentException(
                "The creation request hash must be a hex SHA-256.",
                nameof(creationRequestHash));
        }

        return new Organization
        {
            PublicId = Guid.NewGuid(),
            Name = name,
            IsLegacy = false,
            CreatedByUserId = createdByUserId,
            CreationRequestId = creationRequestId,
            CreationRequestHash = creationRequestHash,
            CreatedUtc = nowUtc.ToUniversalTime()
        };
    }

    /// <param name="name">Already normalised with <see cref="NameText.Normalize"/>.</param>
    public void Rename(
        string name)
    {
        ValidateName(name);
        Name = name;
    }

    private static void ValidateName(
        string name)
    {
        if (name.Length is < MinimumNameLength or > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                $"An organisation name has {MinimumNameLength} to {MaximumNameLength} characters.");
        }
    }
}
