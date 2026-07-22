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

        return Results.Ok(responses);
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

        return Results.Ok(
            BookingResponse.FromBooking(
                booking));
    }

    private static async Task<IResult> CreateAsync(
        SaveBookingRequest request,
        BookingService service,
        CancellationToken cancellationToken)
    {
        var validationErrors =
            BookingRequestValidator.Validate(
                request);

        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(
                validationErrors);
        }

        var result =
            await service.CreateAsync(
                request,
                cancellationToken);

        if (result.Failure ==
            CreateBookingFailure.RoomNotFound)
        {
            return CreateProblem(
                statusCode:
                    StatusCodes.Status404NotFound,
                code:
                    "meeting_room_not_found",
                title:
                    "Meeting room not found",
                detail:
                    $"Meeting room {request.RoomId} does not exist.");
        }

        if (result.Failure ==
            CreateBookingFailure.Overlap)
        {
            return CreateProblem(
                statusCode:
                    StatusCodes.Status409Conflict,
                code:
                    "booking_overlap",
                title:
                    "Meeting room is unavailable",
                detail:
                    "The meeting room is already booked during the selected period.");
        }

        var booking =
            result.Booking
            ?? throw new InvalidOperationException(
                "A successful booking result did not contain a booking.");

        var response =
            BookingResponse.FromBooking(
                booking);

        return Results.Created(
            $"/api/bookings/{booking.Id}",
            response);
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