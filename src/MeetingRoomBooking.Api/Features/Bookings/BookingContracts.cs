namespace MeetingRoomBooking.Api.Features.Bookings;

public sealed record SaveBookingRequest(
    int RoomId,
    string? Title,
    string? BookedBy,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

public sealed record BookingResponse(
    int Id,
    int RoomId,
    string RoomName,
    string Title,
    string BookedBy,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset CreatedUtc)
{
    public static BookingResponse FromBooking(
        Booking booking)
    {
        return new BookingResponse(
            Id: booking.Id,
            RoomId: booking.RoomId,
            RoomName: booking.Room.Name,
            Title: booking.Title,
            BookedBy: booking.BookedBy,
            StartUtc: booking.StartUtc,
            EndUtc: booking.EndUtc,
            CreatedUtc: booking.CreatedUtc);
    }
}

/// <summary>
/// A booking as the organisation endpoints return it: the legacy shape plus
/// what the caller may do with it, so a client can show or hide its edit
/// and delete actions. The API still checks every change itself.
/// </summary>
/// <param name="CreatedByCurrentUser">The caller created it.</param>
/// <param name="CanModify">The caller may change and delete it (see <see cref="BookingActor.MayModify"/>).</param>
public sealed record OrganizationBookingResponse(
    int Id,
    int RoomId,
    string RoomName,
    string Title,
    string BookedBy,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? UpdatedUtc,
    bool CreatedByCurrentUser,
    bool CanModify)
{
    public static OrganizationBookingResponse FromBooking(
        Booking booking,
        BookingActor actor)
    {
        return new OrganizationBookingResponse(
            Id: booking.Id,
            RoomId: booking.RoomId,
            RoomName: booking.Room.Name,
            Title: booking.Title,
            BookedBy: booking.BookedBy,
            StartUtc: booking.StartUtc,
            EndUtc: booking.EndUtc,
            CreatedUtc: booking.CreatedUtc,
            UpdatedUtc: booking.UpdatedUtc,
            CreatedByCurrentUser: booking.CreatedByUserId == actor.UserId,
            CanModify: actor.MayModify(booking));
    }
}

internal static class BookingRequestValidator
{
    internal const string StartNotInFutureMessage =
        "The start time must be later than the current time.";

    public static Dictionary<string, string[]> Validate(
        SaveBookingRequest request)
    {
        var errors =
            new Dictionary<string, string[]>();

        if (request.RoomId <= 0)
        {
            errors["roomId"] =
            [
                "A valid meeting room is required."
            ];
        }

        if (string.IsNullOrWhiteSpace(
                request.Title))
        {
            errors["title"] =
            [
                "A booking title is required."
            ];
        }
        else if (
            request.Title.Trim().Length >
            Booking.MaximumTitleLength)
        {
            errors["title"] =
            [
                $"The booking title cannot exceed {Booking.MaximumTitleLength} characters."
            ];
        }

        if (string.IsNullOrWhiteSpace(
                request.BookedBy))
        {
            errors["bookedBy"] =
            [
                "The name of the person booking the room is required."
            ];
        }
        else if (
            request.BookedBy.Trim().Length >
            Booking.MaximumBookedByLength)
        {
            errors["bookedBy"] =
            [
                $"Booked by cannot exceed {Booking.MaximumBookedByLength} characters."
            ];
        }

        if (request.StartUtc == default)
        {
            errors["startUtc"] =
            [
                "A start time is required."
            ];
        }

        if (request.EndUtc == default)
        {
            errors["endUtc"] =
            [
                "An end time is required."
            ];
        }
        else if (
            request.StartUtc != default &&
            request.EndUtc <= request.StartUtc)
        {
            errors["endUtc"] =
            [
                "The end time must be later than the start time."
            ];
        }

        return errors;
    }
}