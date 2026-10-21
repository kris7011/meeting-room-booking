namespace MeetingRoomBooking.Api.Data;

/// <summary>
/// The organisation that holds every meeting room and booking created before
/// organisations existed. Nobody becomes a member of it automatically.
/// </summary>
public static class LegacyOrganization
{
    public const int Id = 1;

    public const string Name = "Legacy";

    public static readonly Guid PublicId =
        new("bf2b2b52-fb51-4871-b2d7-43d8e25a9e47");

    public static readonly DateTimeOffset CreatedUtc =
        new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
}
