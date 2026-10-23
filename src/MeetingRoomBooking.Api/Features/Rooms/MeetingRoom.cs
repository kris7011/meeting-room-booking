using MeetingRoomBooking.Api.Features.Bookings;
using MeetingRoomBooking.Api.Features.Text;

namespace MeetingRoomBooking.Api.Features.Rooms;

public sealed class MeetingRoom
{
    internal const int MaximumNameLength = 100;

    /// <summary>A sanity limit for typing errors; the database only requires a positive number.</summary>
    internal const int MaximumCapacity = 500;

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

    public bool IsArchived =>
        ArchivedUtc is not null;

    /// <param name="name">Already normalised with <see cref="NameText.Normalize"/>.</param>
    public static MeetingRoom Create(
        int organizationId,
        string name,
        int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(organizationId);

        var room = new MeetingRoom
        {
            OrganizationId = organizationId
        };

        room.Update(name, capacity);

        return room;
    }

    /// <param name="name">Already normalised with <see cref="NameText.Normalize"/>.</param>
    public void Update(
        string name,
        int capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, MaximumNameLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumCapacity);

        Name = name;
        Capacity = capacity;
    }

    /// <summary>Hidden from searches and new bookings; its bookings are kept.</summary>
    public void Archive(
        DateTimeOffset nowUtc)
    {
        ArchivedUtc ??= nowUtc.ToUniversalTime();
    }

    public void Restore()
    {
        ArchivedUtc = null;
    }
}