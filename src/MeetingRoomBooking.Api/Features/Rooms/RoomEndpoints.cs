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
            .WithTags("Rooms")
            .WithOpenApi();

        return endpoints;
    }

    private static async Task<IResult> GetAllAsync(
        BookingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rooms =
            await dbContext.MeetingRooms
                .AsNoTracking()
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