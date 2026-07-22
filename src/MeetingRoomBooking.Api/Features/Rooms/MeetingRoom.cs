using MeetingRoomBooking.Api.Features.Bookings;

namespace MeetingRoomBooking.Api.Features.Rooms;

public sealed class MeetingRoom
{
    private MeetingRoom()
    {
    }

    public int Id { get; private set; }

    public string Name { get; private set; } =
        string.Empty;

    public int Capacity { get; private set; }

    public ICollection<Booking> Bookings
    {
        get;
        private set;
    } = [];
}