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

    public bool IsDisabled =>
        DisabledUtc is not null;

    /// <summary>
    /// A new user, registered from a validated sign-in. The display name is
    /// trimmed and shortened to fit; the contact e-mail is only ever an
    /// address the issuer has verified.
    /// </summary>
    public static User Register(
        string displayName,
        string? contactEmail,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new User
        {
            PublicId = Guid.NewGuid(),
            DisplayName = Truncate(displayName.Trim(), MaximumDisplayNameLength),
            ContactEmail =
                contactEmail is { Length: <= MaximumContactEmailLength }
                    ? contactEmail
                    : null,
            CreatedUtc = nowUtc.ToUniversalTime(),
            LastSeenUtc = nowUtc.ToUniversalTime()
        };
    }

    public void RecordSeen(
        DateTimeOffset nowUtc)
    {
        LastSeenUtc = nowUtc.ToUniversalTime();
    }

    // Never cuts a surrogate pair in half.
    private static string Truncate(
        string value,
        int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var length =
            char.IsHighSurrogate(value[maximumLength - 1])
                ? maximumLength - 1
                : maximumLength;

        return value[..length].TrimEnd();
    }
}
