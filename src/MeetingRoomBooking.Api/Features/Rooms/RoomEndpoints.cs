using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Organizations;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Rooms;

public sealed record MeetingRoomResponse(
    int Id,
    string Name,
    int Capacity);

/// <summary>A room as the organisation endpoints return it.</summary>
/// <param name="ArchivedUtc">Set for an archived room; only Owners and Admins see those.</param>
public sealed record OrganizationRoomResponse(
    int Id,
    string Name,
    int Capacity,
    DateTimeOffset? ArchivedUtc)
{
    public static OrganizationRoomResponse FromRoom(
        MeetingRoom room) =>
        new(room.Id, room.Name, room.Capacity, room.ArchivedUtc);
}

public static class RoomEndpoints
{
    /// <summary>
    /// The original /api/rooms endpoints, always on the legacy organisation.
    /// Mapped in a legacy group (see <see cref="LegacyApiAccess"/>).
    /// </summary>
    public static RouteGroupBuilder MapRoomEndpoints(
        this RouteGroupBuilder group)
    {
        group.WithTags("Rooms");

        group.MapGet(
                "",
                GetAllAsync)
            .WithName("GetMeetingRooms");

        group.MapGet(
                "/available",
                GetAvailableAsync)
            .WithName("GetAvailableMeetingRooms");

        return group;
    }

    /// <summary>
    /// /api/organizations/{organizationId}/rooms: the organisation's rooms
    /// and the availability search, for every member.
    /// </summary>
    public static RouteGroupBuilder MapOrganizationRoomEndpoints(
        this RouteGroupBuilder organization)
    {
        var group =
            organization.MapGroup("/rooms")
                .WithTags("Organisation rooms");

        group.MapGet(
                "",
                GetOrganizationRoomsAsync)
            .WithName("GetOrganizationRooms");

        group.MapGet(
                "/available",
                GetOrganizationAvailableAsync)
            .WithName("GetOrganizationAvailableRooms");

        // Room administration: Owners and Admins.
        group.MapPost(
                "",
                CreateRoomAsync)
            .RequireOwnerOrAdmin()
            .WithName("CreateOrganizationRoom");

        group.MapPut(
                "/{roomId:int}",
                UpdateRoomAsync)
            .RequireOwnerOrAdmin()
            .WithName("UpdateOrganizationRoom");

        group.MapPost(
                "/{roomId:int}/archive",
                ArchiveRoomAsync)
            .RequireOwnerOrAdmin()
            .WithName("ArchiveOrganizationRoom");

        group.MapPost(
                "/{roomId:int}/restore",
                RestoreRoomAsync)
            .RequireOwnerOrAdmin()
            .WithName("RestoreOrganizationRoom");

        return organization;
    }

    private static IQueryable<MeetingRoom> ActiveRoomsOf(
        BookingDbContext dbContext,
        int organizationId)
    {
        return dbContext.MeetingRooms
            .AsNoTracking()
            .Where(
                room =>
                    room.OrganizationId == organizationId
                    && room.ArchivedUtc == null);
    }

    private static Task<IResult> GetAvailableAsync(
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        int? minCapacity,
        BookingDbContext dbContext,
        CancellationToken cancellationToken) =>
        AvailableAsync(
            LegacyOrganization.Id,
            startUtc,
            endUtc,
            minCapacity,
            dbContext,
            room => new MeetingRoomResponse(room.Id, room.Name, room.Capacity),
            cancellationToken);

    private static Task<IResult> GetOrganizationAvailableAsync(
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        int? minCapacity,
        BookingDbContext dbContext,
        CurrentOrganization organization,
        CancellationToken cancellationToken) =>
        AvailableAsync(
            organization.Required().OrganizationId,
            startUtc,
            endUtc,
            minCapacity,
            dbContext,
            OrganizationRoomResponse.FromRoom,
            cancellationToken);

    // Return the smallest fitting free rooms first. Availability is advisory:
    // the SQLite overlap constraint still decides whether a booking succeeds.
    private static async Task<IResult> AvailableAsync<TResponse>(
        int organizationId,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        int? minCapacity,
        BookingDbContext dbContext,
        Func<MeetingRoom, TResponse> toResponse,
        CancellationToken cancellationToken)
    {
        if (startUtc is null ||
            endUtc is null ||
            startUtc == default ||
            endUtc == default ||
            endUtc <= startUtc)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["timeRange"] =
                    [
                        "Provide a valid startUtc and endUtc with endUtc after startUtc."
                    ]
                });
        }

        if (minCapacity is <= 0)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["minCapacity"] = ["Capacity must be a positive number."]
                });
        }

        var minimum = minCapacity ?? 1;
        var start = startUtc.Value.ToUniversalTime();
        var end = endUtc.Value.ToUniversalTime();

        var rooms = await ActiveRoomsOf(dbContext, organizationId)
            .Where(room => room.Capacity >= minimum)
            .Where(room => !dbContext.Bookings.Any(booking =>
                booking.RoomId == room.Id &&
                booking.StartUtc < end &&
                booking.EndUtc > start))
            .OrderBy(room => room.Capacity)
            .ThenBy(room => room.Name)
            .ToArrayAsync(cancellationToken);

        return Results.Ok(rooms.Select(toResponse).ToArray());
    }

    private static async Task<IResult> GetAllAsync(
        BookingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rooms =
            await ActiveRoomsOf(dbContext, LegacyOrganization.Id)
                .OrderBy(room => room.Name)
                .Select(
                    room =>
                        new MeetingRoomResponse(
                            room.Id,
                            room.Name,
                            room.Capacity))
                .ToArrayAsync(cancellationToken);

        return Results.Ok(rooms);
    }

    private static async Task<IResult> CreateRoomAsync(
        SaveRoomRequest request,
        RoomAdministrationService rooms,
        CurrentOrganization organization,
        CancellationToken cancellationToken)
    {
        var caller = organization.Required();
        var result = await rooms.CreateAsync(caller, request, cancellationToken);

        return result.Room is { } room
            ? Results.Created(
                $"/api/organizations/{caller.OrganizationPublicId}/rooms/{room.Id}",
                OrganizationRoomResponse.FromRoom(room))
            : ToProblem(result, roomId: 0);
    }

    private static async Task<IResult> UpdateRoomAsync(
        int roomId,
        SaveRoomRequest request,
        RoomAdministrationService rooms,
        CurrentOrganization organization,
        CancellationToken cancellationToken) =>
        ToResponse(
            await rooms.UpdateAsync(organization.Required(), roomId, request, cancellationToken),
            roomId);

    private static async Task<IResult> ArchiveRoomAsync(
        int roomId,
        RoomAdministrationService rooms,
        CurrentOrganization organization,
        CancellationToken cancellationToken) =>
        ToResponse(
            await rooms.ArchiveAsync(organization.Required(), roomId, cancellationToken),
            roomId);

    private static async Task<IResult> RestoreRoomAsync(
        int roomId,
        RoomAdministrationService rooms,
        CurrentOrganization organization,
        CancellationToken cancellationToken) =>
        ToResponse(
            await rooms.RestoreAsync(organization.Required(), roomId, cancellationToken),
            roomId);

    private static IResult ToResponse(
        RoomResult result,
        int roomId) =>
        result.Room is { } room
            ? Results.Ok(OrganizationRoomResponse.FromRoom(room))
            : ToProblem(result, roomId);

    private static IResult ToProblem(
        RoomResult result,
        int roomId) =>
        result.Failure switch
        {
            RoomFailure.InvalidRequest =>
                Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>()),

            RoomFailure.RoomNotFound =>
                ApiProblems.MeetingRoomNotFound(roomId),

            RoomFailure.NameTaken =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "room_name_taken",
                    "Room name already in use",
                    "Another room of this organisation already has this name."),

            RoomFailure.RoomArchived =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "room_archived",
                    "Room is archived",
                    "Restore the room before changing it."),

            RoomFailure.HasUpcomingBookings =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "room_has_upcoming_bookings",
                    "Room has upcoming bookings",
                    $"The room has {result.UpcomingBookings} booking(s) that have not ended. Move or delete them before archiving the room."),

            _ =>
                throw new ArgumentOutOfRangeException(nameof(result), result.Failure, "Unknown room failure.")
        };

    /// <param name="includeArchived">Owners and Admins only (403 for Members).</param>
    private static async Task<IResult> GetOrganizationRoomsAsync(
        bool? includeArchived,
        BookingDbContext dbContext,
        CurrentOrganization organization,
        CancellationToken cancellationToken)
    {
        var caller =
            organization.Required();

        if (includeArchived == true &&
            !caller.IsOwnerOrAdmin)
        {
            return ApiProblems.OrganizationPermissionDenied(
                "Only Owners and Admins can see archived rooms.");
        }

        var rooms =
            dbContext.MeetingRooms
                .AsNoTracking()
                .Where(room => room.OrganizationId == caller.OrganizationId);

        if (includeArchived != true)
        {
            rooms = rooms.Where(room => room.ArchivedUtc == null);
        }

        var result =
            await rooms
                .OrderBy(room => room.ArchivedUtc != null)
                .ThenBy(room => room.Name)
                .ToArrayAsync(cancellationToken);

        return Results.Ok(result.Select(OrganizationRoomResponse.FromRoom).ToArray());
    }
}
