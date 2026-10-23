using Microsoft.AspNetCore.Mvc;

namespace MeetingRoomBooking.Api.Features;

/// <summary>
/// Problem Details for the signed-in endpoints. Every problem carries a
/// stable <c>code</c> that clients branch on; title and detail are for people.
/// </summary>
public static class ApiProblems
{
    public static IResult Create(
        int statusCode,
        string code,
        string title,
        string detail)
    {
        var problem =
            new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail
            };

        problem.Extensions["code"] = code;

        return Results.Problem(problem);
    }

    public static IResult AuthenticationNotConfigured() =>
        Create(
            StatusCodes.Status503ServiceUnavailable,
            "authentication_not_configured",
            "Sign-in is not configured",
            "The API has no Entra settings, so it cannot validate access tokens.");

    public static IResult UserDisabled() =>
        Create(
            StatusCodes.Status403Forbidden,
            "user_disabled",
            "User disabled",
            "This user account has been disabled.");

    public static IResult UserNotRegistered() =>
        Create(
            StatusCodes.Status403Forbidden,
            "user_not_registered",
            "User not registered",
            "Register with POST /api/me/registration before using this endpoint.");

    /// <summary>
    /// No such organisation, or the caller is not a member of it: the two
    /// look the same, so an organisation's existence is never revealed.
    /// </summary>
    public static IResult OrganizationNotFound() =>
        Create(
            StatusCodes.Status404NotFound,
            "organization_not_found",
            "Organisation not found",
            "The organisation does not exist, or you are not a member of it.");

    /// <summary>The caller is a member, but their role may not do this.</summary>
    public static IResult OrganizationPermissionDenied(
        string detail = "Your role in this organisation does not allow this.") =>
        Create(
            StatusCodes.Status403Forbidden,
            "organization_permission_denied",
            "Not allowed",
            detail);

    public static IResult MeetingRoomNotFound(
        int roomId) =>
        Create(
            StatusCodes.Status404NotFound,
            "meeting_room_not_found",
            "Meeting room not found",
            $"Meeting room {roomId} does not exist.");

    public static IResult BookingNotFound(
        int bookingId) =>
        Create(
            StatusCodes.Status404NotFound,
            "booking_not_found",
            "Booking not found",
            $"Booking {bookingId} does not exist.");
}
