using MeetingRoomBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Bookings;

public enum BookingMutationFailure
{
    None,
    BookingNotFound,
    RoomNotFound,
    Overlap
}

public sealed record BookingMutationResult(
    Booking? Booking,
    BookingMutationFailure Failure)
{
    public static BookingMutationResult Success(
        Booking booking)
    {
        return new BookingMutationResult(
            booking,
            BookingMutationFailure.None);
    }

    public static BookingMutationResult Failed(
        BookingMutationFailure failure)
    {
        return new BookingMutationResult(
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

    public async Task<BookingMutationResult> CreateAsync(
        SaveBookingRequest request,
        CancellationToken cancellationToken)
    {
        var roomExists =
            await RoomExistsAsync(
                request.RoomId,
                cancellationToken);

        if (!roomExists)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.RoomNotFound);
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
                excludedBookingId: null,
                cancellationToken:
                    cancellationToken);

        if (overlaps)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.Overlap);
        }

        var booking =
            Booking.Create(
                roomId: request.RoomId,
                title: request.Title!,
                bookedBy: request.BookedBy!,
                startUtc: startUtc,
                endUtc: endUtc);

        dbContext.Bookings.Add(
            booking);

        await dbContext.SaveChangesAsync(
            cancellationToken);

        await LoadRoomAsync(
            booking,
            cancellationToken);

        return BookingMutationResult.Success(
            booking);
    }

    public async Task<BookingMutationResult> UpdateAsync(
        int bookingId,
        SaveBookingRequest request,
        CancellationToken cancellationToken)
    {
        var booking =
            await dbContext.Bookings
                .SingleOrDefaultAsync(
                    booking =>
                        booking.Id == bookingId,
                    cancellationToken);

        if (booking is null)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.BookingNotFound);
        }

        var roomExists =
            await RoomExistsAsync(
                request.RoomId,
                cancellationToken);

        if (!roomExists)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.RoomNotFound);
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
                excludedBookingId: bookingId,
                cancellationToken:
                    cancellationToken);

        if (overlaps)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.Overlap);
        }

        booking.Update(
            roomId: request.RoomId,
            title: request.Title!,
            bookedBy: request.BookedBy!,
            startUtc: startUtc,
            endUtc: endUtc);

        await dbContext.SaveChangesAsync(
            cancellationToken);

        await LoadRoomAsync(
            booking,
            cancellationToken);

        return BookingMutationResult.Success(
            booking);
    }

    public async Task<bool> DeleteAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking =
            await dbContext.Bookings
                .SingleOrDefaultAsync(
                    booking =>
                        booking.Id == bookingId,
                    cancellationToken);

        if (booking is null)
        {
            return false;
        }

        dbContext.Bookings.Remove(
            booking);

        await dbContext.SaveChangesAsync(
            cancellationToken);

        return true;
    }

    private Task<bool> RoomExistsAsync(
        int roomId,
        CancellationToken cancellationToken)
    {
        return dbContext.MeetingRooms
            .AnyAsync(
                room =>
                    room.Id == roomId,
                cancellationToken);
    }

    private Task<bool> HasOverlapAsync(
        int roomId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int? excludedBookingId,
        CancellationToken cancellationToken)
    {
        return dbContext.Bookings
            .AnyAsync(
                existing =>
                    existing.RoomId == roomId
                    &&
                    (
                        excludedBookingId == null
                        ||
                        existing.Id != excludedBookingId
                    )
                    &&
                    existing.StartUtc < endUtc
                    &&
                    existing.EndUtc > startUtc,
                cancellationToken);
    }

    private async Task LoadRoomAsync(
        Booking booking,
        CancellationToken cancellationToken)
    {
        await dbContext.Entry(booking)
            .Reference(item => item.Room)
            .LoadAsync(cancellationToken);
    }
}