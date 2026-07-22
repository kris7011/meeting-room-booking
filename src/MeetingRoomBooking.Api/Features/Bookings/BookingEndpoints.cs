using Microsoft.AspNetCore.Mvc;

namespace MeetingRoomBooking.Api.Features.Bookings;

public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group =
            endpoints.MapGroup("/api/bookings")
                .WithTags("Bookings");

        group.MapGet(
                "",
                GetAllAsync)
            .WithName("GetBookings")
            .WithOpenApi();

        group.MapGet(
                "/{bookingId:int}",
                GetByIdAsync)
            .WithName("GetBookingById")
            .WithOpenApi();

        group.MapPost(
                "",
                CreateAsync)
            .WithName("CreateBooking")
            .WithOpenApi();

        group.MapPut(
                "/{bookingId:int}",
                UpdateAsync)
            .WithName("UpdateBooking")
            .WithOpenApi();

        group.MapDelete(
                "/{bookingId:int}",
                DeleteAsync)
            .WithName("DeleteBooking")
            .WithOpenApi();

        return endpoints;
    }

    private static async Task<IResult> GetAllAsync(
        BookingService service,
        CancellationToken cancellationToken)
    {
        var bookings =
            await service.GetAllAsync(
                cancellationToken);

        var responses =
            bookings
                .Select(
                    BookingResponse.FromBooking)
                .ToArray();

        return Results.Ok(
            responses);
    }

    private static async Task<IResult> GetByIdAsync(
        int bookingId,
        BookingService service,
        CancellationToken cancellationToken)
    {
        var booking =
            await service.GetByIdAsync(
                bookingId,
                cancellationToken);

        if (booking is null)
        {
            return CreateBookingNotFoundProblem(
                bookingId);
        }

        return Results.Ok(
            BookingResponse.FromBooking(
                booking));
    }

    private static async Task<IResult> CreateAsync(
        SaveBookingRequest request,
        BookingService service,
        CancellationToken cancellationToken)
    {
        var validationResult =
            ValidateRequest(
                request);

        if (validationResult is not null)
        {
            return validationResult;
        }

        var result =
            await service.CreateAsync(
                request,
                cancellationToken);

        var failureResult =
            HandleMutationFailure(
                result.Failure,
                bookingId: null,
                roomId: request.RoomId);

        if (failureResult is not null)
        {
            return failureResult;
        }

        var booking =
            GetSuccessfulBooking(
                result);

        return Results.Created(
            $"/api/bookings/{booking.Id}",
            BookingResponse.FromBooking(
                booking));
    }

    private static async Task<IResult> UpdateAsync(
        int bookingId,
        SaveBookingRequest request,
        BookingService service,
        CancellationToken cancellationToken)
    {
        var validationResult =
            ValidateRequest(
                request);

        if (validationResult is not null)
        {
            return validationResult;
        }

        var result =
            await service.UpdateAsync(
                bookingId,
                request,
                cancellationToken);

        var failureResult =
            HandleMutationFailure(
                result.Failure,
                bookingId,
                request.RoomId);

        if (failureResult is not null)
        {
            return failureResult;
        }

        var booking =
            GetSuccessfulBooking(
                result);

        return Results.Ok(
            BookingResponse.FromBooking(
                booking));
    }

    private static async Task<IResult> DeleteAsync(
        int bookingId,
        BookingService service,
        CancellationToken cancellationToken)
    {
        var wasDeleted =
            await service.DeleteAsync(
                bookingId,
                cancellationToken);

        if (!wasDeleted)
        {
            return CreateBookingNotFoundProblem(
                bookingId);
        }

        return Results.NoContent();
    }

    private static IResult? ValidateRequest(
        SaveBookingRequest request)
    {
        var validationErrors =
            BookingRequestValidator.Validate(
                request);

        if (validationErrors.Count == 0)
        {
            return null;
        }

        return Results.ValidationProblem(
            validationErrors);
    }

    private static IResult? HandleMutationFailure(
        BookingMutationFailure failure,
        int? bookingId,
        int roomId)
    {
        return failure switch
        {
            BookingMutationFailure.None =>
                null,

            BookingMutationFailure.BookingNotFound =>
                CreateBookingNotFoundProblem(
                    bookingId
                    ?? throw new InvalidOperationException(
                        "A booking ID is required for a booking-not-found result.")),

            BookingMutationFailure.RoomNotFound =>
                CreateProblem(
                    statusCode:
                        StatusCodes.Status404NotFound,
                    code:
                        "meeting_room_not_found",
                    title:
                        "Meeting room not found",
                    detail:
                        $"Meeting room {roomId} does not exist."),

            BookingMutationFailure.Overlap =>
                CreateProblem(
                    statusCode:
                        StatusCodes.Status409Conflict,
                    code:
                        "booking_overlap",
                    title:
                        "Meeting room is unavailable",
                    detail:
                        "The meeting room is already booked during the selected period."),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(failure),
                    failure,
                    "Unknown booking mutation failure.")
        };
    }

    private static Booking GetSuccessfulBooking(
        BookingMutationResult result)
    {
        return result.Booking
            ?? throw new InvalidOperationException(
                "A successful booking result did not contain a booking.");
    }

    private static IResult CreateBookingNotFoundProblem(
        int bookingId)
    {
        return CreateProblem(
            statusCode:
                StatusCodes.Status404NotFound,
            code:
                "booking_not_found",
            title:
                "Booking not found",
            detail:
                $"Booking {bookingId} does not exist.");
    }

    private static IResult CreateProblem(
        int statusCode,
        string code,
        string title,
        string detail)
    {
        var problemDetails =
            new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail
            };

        problemDetails.Extensions["code"] =
            code;

        return Results.Problem(
            problemDetails);
    }
}