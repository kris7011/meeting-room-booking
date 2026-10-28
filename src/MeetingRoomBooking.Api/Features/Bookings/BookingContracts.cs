namespace MeetingRoomBooking.Api.Features.Bookings;

public sealed record SaveBookingRequest(
    int RoomId,
    string? Title,
    string? BookedBy,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

/// <summary>
/// A booking as the original endpoints return it. Their shape is fixed, so
/// a booking whose details the caller may not see (see
/// <see cref="BookingActor.MaySeeDetails"/>) has a null title and
/// "booked by" instead of an extra property.
/// </summary>
public sealed record BookingResponse(
    int Id,
    int RoomId,
    string RoomName,
    string? Title,
    string? BookedBy,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset CreatedUtc)
{
    /// <param name="viewer">
    /// The signed-in member asking, or null in anonymous demo mode, which
    /// shows every booking in full.
    /// </param>
    public static BookingResponse FromBooking(
        Booking booking,
        BookingActor? viewer)
    {
        var showDetails =
            viewer?.MaySeeDetails(booking) ?? true;

        return new BookingResponse(
            Id: booking.Id,
            RoomId: booking.RoomId,
            RoomName: booking.Room.Name,
            Title: showDetails ? booking.Title : null,
            BookedBy: showDetails ? booking.BookedBy : null,
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
/// <param name="Title">Null when <paramref name="DetailsHidden"/>.</param>
/// <param name="BookedBy">Null when <paramref name="DetailsHidden"/>.</param>
/// <param name="CreatedByCurrentUser">The caller created it.</param>
/// <param name="CanModify">The caller may change and delete it (see <see cref="BookingActor.MayModify"/>).</param>
/// <param name="DetailsHidden">
/// The caller is a Member and someone else made the booking: only its room
/// and period are shown (see <see cref="BookingActor.MaySeeDetails"/>).
/// </param>
public sealed record OrganizationBookingResponse(
    int Id,
    int RoomId,
    string RoomName,
    string? Title,
    string? BookedBy,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? UpdatedUtc,
    bool CreatedByCurrentUser,
    bool CanModify,
    bool DetailsHidden)
{
    public static OrganizationBookingResponse FromBooking(
        Booking booking,
        BookingActor actor)
    {
        var showDetails =
            actor.MaySeeDetails(booking);

        return new OrganizationBookingResponse(
            Id: booking.Id,
            RoomId: booking.RoomId,
            RoomName: booking.Room.Name,
            Title: showDetails ? booking.Title : null,
            BookedBy: showDetails ? booking.BookedBy : null,
            StartUtc: booking.StartUtc,
            EndUtc: booking.EndUtc,
            CreatedUtc: booking.CreatedUtc,
            UpdatedUtc: booking.UpdatedUtc,
            CreatedByCurrentUser: booking.CreatedByUserId == actor.UserId,
            CanModify: actor.MayModify(booking),
            DetailsHidden: !showDetails);
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