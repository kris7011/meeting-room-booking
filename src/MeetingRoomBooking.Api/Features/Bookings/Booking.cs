using MeetingRoomBooking.Api.Features.Rooms;

namespace MeetingRoomBooking.Api.Features.Bookings;

public sealed class Booking
{
    private const int MaximumTitleLength = 200;
    private const int MaximumBookedByLength = 120;

    private Booking()
    {
    }

    public int Id { get; private set; }

    public int RoomId { get; private set; }

    public string Title { get; private set; } =
        string.Empty;

    public string BookedBy { get; private set; } =
        string.Empty;

    public DateTimeOffset StartUtc
    {
        get;
        private set;
    }

    public DateTimeOffset EndUtc
    {
        get;
        private set;
    }

    public DateTimeOffset CreatedUtc
    {
        get;
        private set;
    }

    public MeetingRoom Room { get; private set; } =
        null!;

    public static Booking Create(
        int roomId,
        string title,
        string bookedBy,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        var booking = new Booking
        {
            CreatedUtc = DateTimeOffset.UtcNow
        };

        booking.ApplyDetails(
            roomId,
            title,
            bookedBy,
            startUtc,
            endUtc);

        return booking;
    }

    public void Update(
        int roomId,
        string title,
        string bookedBy,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        ApplyDetails(
            roomId,
            title,
            bookedBy,
            startUtc,
            endUtc);
    }

    private void ApplyDetails(
        int roomId,
        string title,
        string bookedBy,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        if (roomId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roomId),
                "A valid meeting room is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException(
                "A booking title is required.",
                nameof(title));
        }

        var trimmedTitle = title.Trim();

        if (trimmedTitle.Length > MaximumTitleLength)
        {
            throw new ArgumentException(
                $"The booking title cannot exceed {MaximumTitleLength} characters.",
                nameof(title));
        }

        if (string.IsNullOrWhiteSpace(bookedBy))
        {
            throw new ArgumentException(
                "The name of the person booking the room is required.",
                nameof(bookedBy));
        }

        var trimmedBookedBy = bookedBy.Trim();

        if (trimmedBookedBy.Length >
            MaximumBookedByLength)
        {
            throw new ArgumentException(
                $"Booked by cannot exceed {MaximumBookedByLength} characters.",
                nameof(bookedBy));
        }

        if (startUtc == default)
        {
            throw new ArgumentException(
                "A start time is required.",
                nameof(startUtc));
        }

        if (endUtc == default)
        {
            throw new ArgumentException(
                "An end time is required.",
                nameof(endUtc));
        }

        if (endUtc <= startUtc)
        {
            throw new ArgumentException(
                "The end time must be later than the start time.",
                nameof(endUtc));
        }

        RoomId = roomId;
        Title = trimmedTitle;
        BookedBy = trimmedBookedBy;
        StartUtc = startUtc.ToUniversalTime();
        EndUtc = endUtc.ToUniversalTime();
    }
}