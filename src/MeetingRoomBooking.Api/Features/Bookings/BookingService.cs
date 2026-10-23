using Microsoft.Data.Sqlite;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Organizations;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Bookings;

public enum BookingMutationFailure
{
    None,
    BookingNotFound,
    RoomNotFound,
    Overlap,
    /// <summary>
    /// The (changed) period does not start after the server's current time.
    /// </summary>
    StartNotInFuture,
    /// <summary>
    /// The booking exists in the organisation, but the caller may not change
    /// or delete it (a Member and someone else's booking).
    /// </summary>
    PermissionDenied
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

/// <summary>
/// The signed-in member who makes a booking change, from their membership.
/// Null for the legacy endpoints in anonymous demo mode.
/// </summary>
public sealed record BookingActor(
    int UserId,
    MembershipRole Role)
{
    public static BookingActor From(
        OrganizationCaller caller) =>
        new(caller.UserId, caller.Role);

    /// <summary>
    /// Owners and Admins may change and delete every booking of their
    /// organisation; Members only bookings they created themselves. Bookings
    /// from before sign-in have no creator, so only Owners and Admins can
    /// change them.
    /// </summary>
    public bool MayModify(
        Booking booking) =>
        Role is MembershipRole.Owner or MembershipRole.Admin
        || booking.CreatedByUserId == UserId;
}

/// <summary>
/// Bookings of one organisation. Every query and change is limited to the
/// organisation it is given, so a booking or room ID from another
/// organisation is simply "not found".
/// </summary>
/// <remarks>
/// Changes run in a <c>BEGIN IMMEDIATE</c> transaction, so the checks (room
/// still active, no overlap) and the write cannot be interleaved with
/// another writer. The SQLite overlap triggers stay the final word.
/// </remarks>
public sealed class BookingService(
    BookingDbContext dbContext,
    TimeProvider timeProvider)
{
    private IQueryable<Booking> BookingsOf(
        int organizationId) =>
        dbContext.Bookings
            .Where(
                booking =>
                    booking.OrganizationId == organizationId);

    /// <param name="fromUtc">Only bookings that end after this, when set.</param>
    /// <param name="toUtc">Only bookings that start before this, when set.</param>
    public async Task<IReadOnlyCollection<Booking>>
        GetAllAsync(
            int organizationId,
            CancellationToken cancellationToken,
            DateTimeOffset? fromUtc = null,
            DateTimeOffset? toUtc = null)
    {
        var bookings =
            BookingsOf(organizationId)
                .AsNoTracking();

        if (fromUtc is { } from)
        {
            var start = from.ToUniversalTime();
            bookings = bookings.Where(booking => booking.EndUtc > start);
        }

        if (toUtc is { } to)
        {
            var end = to.ToUniversalTime();
            bookings = bookings.Where(booking => booking.StartUtc < end);
        }

        return await bookings
            .Include(booking => booking.Room)
            .OrderBy(booking => booking.StartUtc)
            .ThenBy(booking => booking.Room.Name)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<Booking?> GetByIdAsync(
        int organizationId,
        int bookingId,
        CancellationToken cancellationToken)
    {
        return await BookingsOf(organizationId)
            .AsNoTracking()
            .Include(booking => booking.Room)
            .SingleOrDefaultAsync(
                booking =>
                    booking.Id == bookingId,
                cancellationToken);
    }

    public async Task<BookingMutationResult> CreateAsync(
        int organizationId,
        SaveBookingRequest request,
        BookingActor? actor,
        CancellationToken cancellationToken)
    {
        var startUtc =
            request.StartUtc.ToUniversalTime();

        var endUtc =
            request.EndUtc.ToUniversalTime();

        var nowUtc =
            timeProvider.GetUtcNow();

        // Checked before the room lookup: invalid input is a 400, like the
        // request validator's errors, before a 404 for an unknown room.
        if (startUtc <= nowUtc)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.StartNotInFuture);
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var roomExists =
            await ActiveRoomExistsAsync(
                organizationId,
                request.RoomId,
                cancellationToken);

        if (!roomExists)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.RoomNotFound);
        }

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
                organizationId: organizationId,
                roomId: request.RoomId,
                title: request.Title!,
                bookedBy: request.BookedBy!,
                startUtc: startUtc,
                endUtc: endUtc,
                createdUtc: nowUtc,
                createdByUserId: actor?.UserId);

        dbContext.Bookings.Add(
            booking);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (IsOverlapConstraint(exception))
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.Overlap);
        }

        await LoadRoomAsync(
            booking,
            cancellationToken);

        return BookingMutationResult.Success(
            booking);
    }

    public async Task<BookingMutationResult> UpdateAsync(
        int organizationId,
        int bookingId,
        SaveBookingRequest request,
        BookingActor? actor,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking =
            await BookingsOf(organizationId)
                .SingleOrDefaultAsync(
                    booking =>
                        booking.Id == bookingId,
                    cancellationToken);

        if (booking is null)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.BookingNotFound);
        }

        if (actor is not null &&
            !actor.MayModify(booking))
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.PermissionDenied);
        }

        var startUtc =
            request.StartUtc.ToUniversalTime();

        var endUtc =
            request.EndUtc.ToUniversalTime();

        var nowUtc =
            timeProvider.GetUtcNow();

        // A booking cannot be moved into the past. Changes that keep the
        // period as it is (e.g. a corrected title) stay possible, also for
        // bookings that have already taken place.
        var periodChanged =
            startUtc != booking.StartUtc
            || endUtc != booking.EndUtc;

        if (periodChanged
            && startUtc <= nowUtc)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.StartNotInFuture);
        }

        // A booking may stay in a room that has since been archived as long
        // as neither the room nor the period changes; it cannot be moved
        // into or within an archived room.
        var keepsRoomAndPeriod =
            request.RoomId == booking.RoomId
            && !periodChanged;

        var roomExists =
            keepsRoomAndPeriod
            || await ActiveRoomExistsAsync(
                organizationId,
                request.RoomId,
                cancellationToken);

        if (!roomExists)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.RoomNotFound);
        }

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
            endUtc: endUtc,
            updatedUtc: nowUtc,
            updatedByUserId: actor?.UserId);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (IsOverlapConstraint(exception))
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.Overlap);
        }

        await LoadRoomAsync(
            booking,
            cancellationToken);

        return BookingMutationResult.Success(
            booking);
    }

    public async Task<BookingMutationFailure> DeleteAsync(
        int organizationId,
        int bookingId,
        BookingActor? actor,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking =
            await BookingsOf(organizationId)
                .SingleOrDefaultAsync(
                    booking =>
                        booking.Id == bookingId,
                    cancellationToken);

        if (booking is null)
        {
            return BookingMutationFailure.BookingNotFound;
        }

        if (actor is not null &&
            !actor.MayModify(booking))
        {
            return BookingMutationFailure.PermissionDenied;
        }

        dbContext.Bookings.Remove(
            booking);

        await dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return BookingMutationFailure.None;
    }

    private static bool IsOverlapConstraint(
        DbUpdateException exception)
    {
        return exception.InnerException is SqliteException sqlite
            && sqlite.SqliteErrorCode == 19
            && sqlite.Message.Contains(
                "booking_overlap",
                StringComparison.Ordinal);
    }

    private Task<bool> ActiveRoomExistsAsync(
        int organizationId,
        int roomId,
        CancellationToken cancellationToken)
    {
        return dbContext.MeetingRooms
            .AnyAsync(
                room =>
                    room.Id == roomId
                    && room.OrganizationId == organizationId
                    && room.ArchivedUtc == null,
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
