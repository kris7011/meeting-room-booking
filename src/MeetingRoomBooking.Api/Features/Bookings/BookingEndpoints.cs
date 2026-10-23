using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Organizations;

namespace MeetingRoomBooking.Api.Features.Bookings;

public static class BookingEndpoints
{
    /// <summary>
    /// The original /api/bookings endpoints, always on the legacy
    /// organisation. Mapped in a legacy group: anonymous only in demo mode,
    /// otherwise for members of the legacy organisation (see
    /// <see cref="LegacyApiAccess"/>), with the same rules as the
    /// organisation endpoints.
    /// </summary>
    public static RouteGroupBuilder MapBookingEndpoints(
        this RouteGroupBuilder group)
    {
        group.WithTags("Bookings");

        group.MapGet(
                "",
                GetAllAsync)
            .WithName("GetBookings");

        group.MapGet(
                "/{bookingId:int}",
                GetByIdAsync)
            .WithName("GetBookingById");

        group.MapPost(
                "",
                CreateAsync)
            .WithName("CreateBooking");

        group.MapPut(
                "/{bookingId:int}",
                UpdateAsync)
            .WithName("UpdateBooking");

        group.MapDelete(
                "/{bookingId:int}",
                DeleteAsync)
            .WithName("DeleteBooking");

        return group;
    }

    /// <summary>
    /// /api/organizations/{organizationId}/bookings: every member may list,
    /// read and create bookings; Owners and Admins may change and delete all
    /// of them, Members only their own.
    /// </summary>
    public static RouteGroupBuilder MapOrganizationBookingEndpoints(
        this RouteGroupBuilder organization)
    {
        var group =
            organization.MapGroup("/bookings")
                .WithTags("Organisation bookings");

        group.MapGet(
                "",
                GetOrganizationBookingsAsync)
            .WithName("GetOrganizationBookings");

        group.MapGet(
                "/{bookingId:int}",
                GetOrganizationBookingAsync)
            .WithName("GetOrganizationBooking");

        group.MapPost(
                "",
                CreateOrganizationBookingAsync)
            .WithName("CreateOrganizationBooking");

        group.MapPut(
                "/{bookingId:int}",
                UpdateOrganizationBookingAsync)
            .WithName("UpdateOrganizationBooking");

        group.MapDelete(
                "/{bookingId:int}",
                DeleteOrganizationBookingAsync)
            .WithName("DeleteOrganizationBooking");

        return organization;
    }

    // Legacy endpoints

    private static async Task<IResult> GetAllAsync(
        BookingService service,
        CancellationToken cancellationToken)
    {
        var bookings =
            await service.GetAllAsync(
                LegacyOrganization.Id,
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
                LegacyOrganization.Id,
                bookingId,
                cancellationToken);

        if (booking is null)
        {
            return ApiProblems.BookingNotFound(
                bookingId);
        }

        return Results.Ok(
            BookingResponse.FromBooking(
                booking));
    }

    private static async Task<IResult> CreateAsync(
        SaveBookingRequest request,
        BookingService service,
        CurrentOrganization organization,
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
                LegacyOrganization.Id,
                request,
                ActorOf(organization),
                cancellationToken);

        if (HandleMutationFailure(result.Failure, bookingId: null, roomId: request.RoomId) is { } failure)
        {
            return failure;
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
        CurrentOrganization organization,
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
                LegacyOrganization.Id,
                bookingId,
                request,
                ActorOf(organization),
                cancellationToken);

        if (HandleMutationFailure(result.Failure, bookingId, request.RoomId) is { } failure)
        {
            return failure;
        }

        return Results.Ok(
            BookingResponse.FromBooking(
                GetSuccessfulBooking(
                    result)));
    }

    private static async Task<IResult> DeleteAsync(
        int bookingId,
        BookingService service,
        CurrentOrganization organization,
        CancellationToken cancellationToken)
    {
        var failure =
            await service.DeleteAsync(
                LegacyOrganization.Id,
                bookingId,
                ActorOf(organization),
                cancellationToken);

        return HandleMutationFailure(failure, bookingId, roomId: 0)
            ?? Results.NoContent();
    }

    // In anonymous demo mode nobody is signed in: changes have no actor.
    private static BookingActor? ActorOf(
        CurrentOrganization organization) =>
        organization.Caller is { } caller
            ? BookingActor.From(caller)
            : null;

    // Organisation endpoints

    private static async Task<IResult> GetOrganizationBookingsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        BookingService service,
        CurrentOrganization organization,
        CancellationToken cancellationToken)
    {
        if (fromUtc is not null &&
            toUtc is not null &&
            toUtc <= fromUtc)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["toUtc"] = ["toUtc must be later than fromUtc."]
                });
        }

        var caller =
            organization.Required();

        var actor =
            BookingActor.From(caller);

        var bookings =
            await service.GetAllAsync(
                caller.OrganizationId,
                cancellationToken,
                fromUtc,
                toUtc);

        return Results.Ok(
            bookings
                .Select(booking => OrganizationBookingResponse.FromBooking(booking, actor))
                .ToArray());
    }

    private static async Task<IResult> GetOrganizationBookingAsync(
        int bookingId,
        BookingService service,
        CurrentOrganization organization,
        CancellationToken cancellationToken)
    {
        var caller =
            organization.Required();

        var booking =
            await service.GetByIdAsync(
                caller.OrganizationId,
                bookingId,
                cancellationToken);

        return booking is null
            ? ApiProblems.BookingNotFound(bookingId)
            : Results.Ok(
                OrganizationBookingResponse.FromBooking(
                    booking,
                    BookingActor.From(caller)));
    }

    private static async Task<IResult> CreateOrganizationBookingAsync(
        SaveBookingRequest request,
        BookingService service,
        CurrentOrganization organization,
        CancellationToken cancellationToken)
    {
        var caller =
            organization.Required();

        // "Booked by" defaults to the signed-in user's name.
        request =
            WithDefaultBookedBy(
                request,
                caller);

        if (ValidateRequest(request) is { } invalid)
        {
            return invalid;
        }

        var actor =
            BookingActor.From(caller);

        var result =
            await service.CreateAsync(
                caller.OrganizationId,
                request,
                actor,
                cancellationToken);

        if (HandleMutationFailure(result.Failure, bookingId: null, roomId: request.RoomId) is { } failure)
        {
            return failure;
        }

        var booking =
            GetSuccessfulBooking(
                result);

        return Results.Created(
            $"/api/organizations/{caller.OrganizationPublicId}/bookings/{booking.Id}",
            OrganizationBookingResponse.FromBooking(
                booking,
                actor));
    }

    private static async Task<IResult> UpdateOrganizationBookingAsync(
        int bookingId,
        SaveBookingRequest request,
        BookingService service,
        CurrentOrganization organization,
        CancellationToken cancellationToken)
    {
        var caller =
            organization.Required();

        request =
            WithDefaultBookedBy(
                request,
                caller);

        if (ValidateRequest(request) is { } invalid)
        {
            return invalid;
        }

        var actor =
            BookingActor.From(caller);

        var result =
            await service.UpdateAsync(
                caller.OrganizationId,
                bookingId,
                request,
                actor,
                cancellationToken);

        if (HandleMutationFailure(result.Failure, bookingId, request.RoomId) is { } failure)
        {
            return failure;
        }

        return Results.Ok(
            OrganizationBookingResponse.FromBooking(
                GetSuccessfulBooking(result),
                actor));
    }

    private static async Task<IResult> DeleteOrganizationBookingAsync(
        int bookingId,
        BookingService service,
        CurrentOrganization organization,
        CancellationToken cancellationToken)
    {
        var caller =
            organization.Required();

        var failure =
            await service.DeleteAsync(
                caller.OrganizationId,
                bookingId,
                BookingActor.From(caller),
                cancellationToken);

        return HandleMutationFailure(failure, bookingId, roomId: 0)
            ?? Results.NoContent();
    }

    private static SaveBookingRequest WithDefaultBookedBy(
        SaveBookingRequest request,
        OrganizationCaller caller) =>
        string.IsNullOrWhiteSpace(request.BookedBy)
            ? request with { BookedBy = caller.DisplayName }
            : request;

    // Shared

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
                ApiProblems.BookingNotFound(
                    bookingId
                    ?? throw new InvalidOperationException(
                        "A booking ID is required for a booking-not-found result.")),

            BookingMutationFailure.RoomNotFound =>
                ApiProblems.MeetingRoomNotFound(
                    roomId),

            BookingMutationFailure.Overlap =>
                ApiProblems.Create(
                    statusCode:
                        StatusCodes.Status409Conflict,
                    code:
                        "booking_overlap",
                    title:
                        "Meeting room is unavailable",
                    detail:
                        "The meeting room is already booked during the selected period."),

            BookingMutationFailure.StartNotInFuture =>
                Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["startUtc"] =
                        [
                            BookingRequestValidator.StartNotInFutureMessage
                        ]
                    }),

            BookingMutationFailure.PermissionDenied =>
                ApiProblems.Create(
                    statusCode:
                        StatusCodes.Status403Forbidden,
                    code:
                        "booking_permission_denied",
                    title:
                        "Not your booking",
                    detail:
                        "Members can only change and delete bookings they made themselves. Ask an Owner or Admin."),

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
}
