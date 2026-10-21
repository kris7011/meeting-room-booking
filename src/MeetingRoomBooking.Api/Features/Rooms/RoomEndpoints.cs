using MeetingRoomBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Rooms;

public sealed record MeetingRoomResponse(
    int Id,
    string Name,
    int Capacity);

public static class RoomEndpoints
{
    public static IEndpointRouteBuilder MapRoomEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/rooms",
                GetAllAsync)
            .WithName("GetMeetingRooms")
            .WithTags("Rooms");

        endpoints.MapGet(
                "/api/rooms/available",
                GetAvailableAsync)
            .WithName("GetAvailableMeetingRooms")
            .WithTags("Rooms");

        return endpoints;
    }

    // The original, unauthenticated endpoints only ever list the legacy
    // organisation's rooms, so rooms of organisations created later stay
    // hidden from callers who have not signed in.
    private static IQueryable<MeetingRoom> LegacyRooms(
        BookingDbContext dbContext)
    {
        return dbContext.MeetingRooms
            .AsNoTracking()
            .Where(
                room =>
                    room.OrganizationId == LegacyOrganization.Id
                    && room.ArchivedUtc == null);
    }

    // Return the smallest fitting free rooms first. Availability is advisory:
    // the SQLite overlap constraint still decides whether a booking succeeds.
    private static async Task<IResult> GetAvailableAsync(
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        int? minCapacity,
        BookingDbContext dbContext,
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

        var rooms = await LegacyRooms(dbContext)
            .Where(room => room.Capacity >= minimum)
            .Where(room => !dbContext.Bookings.Any(booking =>
                booking.RoomId == room.Id &&
                booking.StartUtc < end &&
                booking.EndUtc > start))
            .OrderBy(room => room.Capacity)
            .ThenBy(room => room.Name)
            .Select(room => new MeetingRoomResponse(
                room.Id, room.Name, room.Capacity))
            .ToArrayAsync(cancellationToken);

        return Results.Ok(rooms);
    }

    private static async Task<IResult> GetAllAsync(
        BookingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rooms =
            await LegacyRooms(dbContext)
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
}