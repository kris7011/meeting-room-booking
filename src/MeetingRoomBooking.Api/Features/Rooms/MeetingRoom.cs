using MeetingRoomBooking.Api.Features.Bookings;

namespace MeetingRoomBooking.Api.Features.Rooms;

public sealed class MeetingRoom
{
    private MeetingRoom()
    {
    }

    public int Id { get; private set; }

    /// <summary>
    /// The organisation the room belongs to. A room never moves to another
    /// organisation, so this cannot change once the room exists.
    /// </summary>
    public int OrganizationId { get; private set; }

    public string Name { get; private set; } =
        string.Empty;

    public int Capacity { get; private set; }

    /// <summary>
    /// Set instead of deleting a room, so its past bookings are kept.
    /// </summary>
    public DateTimeOffset? ArchivedUtc { get; private set; }

    public ICollection<Booking> Bookings
    {
        get;
        private set;
    } = [];
}