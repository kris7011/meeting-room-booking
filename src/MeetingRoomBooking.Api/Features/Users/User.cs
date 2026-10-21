namespace MeetingRoomBooking.Api.Features.Users;

/// <summary>
/// A person's stable internal identity, independent of how they sign in.
/// Clients only ever see <see cref="PublicId"/>.
/// </summary>
public sealed class User
{
    internal const int MaximumDisplayNameLength = 120;
    internal const int MaximumContactEmailLength = 254;

    private User()
    {
    }

    public int Id { get; private set; }

    public Guid PublicId { get; private set; }

    public string DisplayName { get; private set; } =
        string.Empty;

    /// <summary>For contact only; it never grants access.</summary>
    public string? ContactEmail { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? LastSeenUtc { get; private set; }

    public DateTimeOffset? DisabledUtc { get; private set; }
}
