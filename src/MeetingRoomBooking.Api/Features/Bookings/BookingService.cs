using MeetingRoomBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Bookings;

public enum CreateBookingFailure
{
    None,
    RoomNotFound,
    Overlap
}

public sealed record CreateBookingResult(
    Booking? Booking,
    CreateBookingFailure Failure)
{
    public static CreateBookingResult Success(
        Booking booking)
    {
        return new CreateBookingResult(
            booking,
            CreateBookingFailure.None);
    }

    public static CreateBookingResult Failed(
        CreateBookingFailure failure)
    {
        return new CreateBookingResult(
            Booking: null,
            Failure: failure);
    }
}

public sealed class BookingService(
    BookingDbContext dbContext)
{
    public async Task<IReadOnlyCollection<Booking>>
        GetAllAsync(
            CancellationToken cancellationToken)
    {
        return await dbContext.Bookings
            .AsNoTracking()
            .Include(booking => booking.Room)
            .OrderBy(booking => booking.StartUtc)
            .ThenBy(booking => booking.Room.Name)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<Booking?> GetByIdAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Bookings
            .AsNoTracking()
            .Include(booking => booking.Room)
            .SingleOrDefaultAsync(
                booking =>
                    booking.Id == bookingId,
                cancellationToken);
    }

    public async Task<CreateBookingResult> CreateAsync(
        SaveBookingRequest request,
        CancellationToken cancellationToken)
    {
        var roomExists =
            await dbContext.MeetingRooms
                .AsNoTracking()
                .AnyAsync(
                    room =>
                        room.Id == request.RoomId,
                    cancellationToken);

        if (!roomExists)
        {
            return CreateBookingResult.Failed(
                CreateBookingFailure.RoomNotFound);
        }

        var startUtc =
            request.StartUtc.ToUniversalTime();

        var endUtc =
            request.EndUtc.ToUniversalTime();

        var overlaps =
            await HasOverlapAsync(
                roomId: request.RoomId,
                startUtc: startUtc,
                endUtc: endUtc,
                excludedBookingId: 0,
                cancellationToken:
                    cancellationToken);

        if (overlaps)
        {
            return CreateBookingResult.Failed(
                CreateBookingFailure.Overlap);
        }

        var booking =
            Booking.Create(
                roomId: request.RoomId,
                title: request.Title!,
                bookedBy: request.BookedBy!,
                startUtc: startUtc,
                endUtc: endUtc);

        dbContext.Bookings.Add(booking);

        await dbContext.SaveChangesAsync(
            cancellationToken);

        await dbContext.Entry(booking)
            .Reference(item => item.Room)
            .LoadAsync(cancellationToken);

        return CreateBookingResult.Success(
            booking);
    }

    private Task<bool> HasOverlapAsync(
        int roomId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int excludedBookingId,
        CancellationToken cancellationToken)
    {
        return dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(
                existing =>
                    existing.RoomId == roomId
                    &&
                    existing.Id != excludedBookingId
                    &&
                    existing.StartUtc < endUtc
                    &&
                    existing.EndUtc > startUtc,
                cancellationToken);
    }
}