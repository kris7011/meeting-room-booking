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
}
