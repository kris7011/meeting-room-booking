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
    PermissionDenied,

    /// <summary>The Idempotency-Key was used before with another request.</summary>
    IdempotencyKeyReused,

    /// <summary>
    /// The Idempotency-Key's earlier request created a booking that has
    /// since been deleted.
    /// </summary>
    CreatedBookingDeleted
}

/// <param name="Replayed">
/// True when an Idempotency-Key repeat returns the booking an earlier
/// request created, instead of creating one.
/// </param>
public sealed record BookingMutationResult(
    Booking? Booking,
    BookingMutationFailure Failure,
    bool Replayed = false)
{
    public static BookingMutationResult Success(
        Booking booking,
        bool replayed = false)
    {
        return new BookingMutationResult(
            booking,
            BookingMutationFailure.None,
            replayed);
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
/// <remarks>
/// For a change, <see cref="BookingService"/> builds it from the membership
/// as it is under the write lock, not from the request's access snapshot.
/// </remarks>
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

    /// <summary>
    /// Owners and Admins see the title and "booked by" of every booking of
    /// their organisation; Members only of bookings they created. Every
    /// member sees the room and the period of all bookings, so they can see
    /// when a room is taken. Bookings from before sign-in have no creator, so
    /// their details are hidden from Members too.
    /// </summary>
    /// <remarks>
    /// The same rule as <see cref="MayModify"/> today, kept separate because
    /// seeing and changing are different permissions.
    /// </remarks>
    public bool MaySeeDetails(
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
/// Changes run in a <c>BEGIN IMMEDIATE</c> transaction, so the checks (the
/// caller's access, room still active, no overlap) and the write cannot be
/// interleaved with another writer. The SQLite overlap triggers stay the
/// final word.
/// </remarks>
public sealed class BookingService(
    BookingDbContext dbContext,
    OrganizationAccessService organizationAccess,
    TimeProvider timeProvider,
    ILogger<BookingService> logger)
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

    /// <param name="idempotency">
    /// The request's Idempotency-Key, or null without one. With a key, the
    /// booking and the key are stored in one transaction; a repeat by the
    /// same caller in the same organisation returns that booking.
    /// </param>
    /// <remarks>
    /// Order under the write lock: the caller's current access first, so a
    /// repeat never returns a booking to someone who has since lost access;
    /// then the key, so a repeat is answered even after the start has passed
    /// or the room was archived; then the checks for a new booking.
    /// </remarks>
    public async Task<BookingMutationResult> CreateAsync(
        int organizationId,
        SaveBookingRequest request,
        OrganizationCaller? caller,
        IdempotentBookingRequest? idempotency,
        CancellationToken cancellationToken)
    {
        var startUtc =
            request.StartUtc.ToUniversalTime();

        var endUtc =
            request.EndUtc.ToUniversalTime();

        var nowUtc =
            timeProvider.GetUtcNow();

        // Checked before the room lookup: invalid input is a 400, like the
        // request validator's errors, before a 404 for an unknown room. A
        // keyed request is checked after its key instead (see remarks).
        if (idempotency is null &&
            startUtc <= nowUtc)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.StartNotInFuture);
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var actor =
            await CurrentActorAsync(
                organizationId,
                caller,
                cancellationToken);

        if (idempotency is not null)
        {
            if (await ReplayAsync(organizationId, actor, idempotency, nowUtc, cancellationToken)
                is { } replayed)
            {
                return replayed;
            }

            if (startUtc <= nowUtc)
            {
                return BookingMutationResult.Failed(
                    BookingMutationFailure.StartNotInFuture);
            }
        }

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

            // In the same transaction as the booking: after a crash either
            // both exist or neither does, so a repeat can never create a
            // second booking, nor find a key without its booking.
            if (idempotency is not null)
            {
                dbContext.BookingCreationRequests.Add(
                    BookingCreationRequest.Record(
                        organizationId,
                        actor?.UserId,
                        idempotency.Key,
                        idempotency.RequestHash,
                        booking.Id,
                        nowUtc));

                await dbContext.SaveChangesAsync(cancellationToken);
            }

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

    /// <summary>
    /// The answer to a repeated Idempotency-Key, or null when the key is new
    /// to this caller and organisation. Called under the write lock, after
    /// the caller's access was checked again. Deletes expired keys first, so
    /// an expired key counts as new; the deletion commits with a new booking
    /// and is rolled back with a refused request.
    /// </summary>
    /// <remarks>
    /// The lookup and the insert in <see cref="CreateAsync"/> run under the
    /// same write lock, so two requests with one key cannot both miss it; the
    /// unique indexes on the table are the database's backstop.
    /// </remarks>
    private async Task<BookingMutationResult?> ReplayAsync(
        int organizationId,
        BookingActor? actor,
        IdempotentBookingRequest idempotency,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var expiredBefore =
            nowUtc - BookingCreationRequest.RetentionPeriod;

        await dbContext.BookingCreationRequests
            .Where(request => request.CreatedUtc < expiredBefore)
            .ExecuteDeleteAsync(cancellationToken);

        var userId =
            actor?.UserId;

        var earlier =
            await dbContext.BookingCreationRequests
                .AsNoTracking()
                .Where(
                    request =>
                        request.OrganizationId == organizationId &&
                        request.UserId == userId &&
                        request.IdempotencyKey == idempotency.Key)
                .Select(
                    request =>
                        new
                        {
                            request.RequestHash,
                            request.BookingId
                        })
                .SingleOrDefaultAsync(cancellationToken);

        if (earlier is null)
        {
            return null;
        }

        if (!string.Equals(earlier.RequestHash, idempotency.RequestHash, StringComparison.Ordinal))
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.IdempotencyKeyReused);
        }

        var booking =
            earlier.BookingId is { } bookingId
                ? await BookingsOf(organizationId)
                    .AsNoTracking()
                    .Include(candidate => candidate.Room)
                    .SingleOrDefaultAsync(
                        candidate =>
                            candidate.Id == bookingId,
                        cancellationToken)
                : null;

        if (booking is null)
        {
            return BookingMutationResult.Failed(
                BookingMutationFailure.CreatedBookingDeleted);
        }

        logger.LogInformation(
            "Repeated booking creation answered with booking {BookingId} in organisation {OrganizationId}.",
            booking.Id,
            organizationId);

        return BookingMutationResult.Success(
            booking,
            replayed: true);
    }

    public async Task<BookingMutationResult> UpdateAsync(
        int organizationId,
        int bookingId,
        SaveBookingRequest request,
        OrganizationCaller? caller,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var actor =
            await CurrentActorAsync(
                organizationId,
                caller,
                cancellationToken);

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
        OrganizationCaller? caller,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var actor =
            await CurrentActorAsync(
                organizationId,
                caller,
                cancellationToken);

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

    /// <summary>
    /// The caller as they are now, under the write lock (null in anonymous
    /// demo mode). A caller who has since been disabled or removed is refused
    /// (<see cref="OrganizationAccessDeniedException"/>); a demoted one gets
    /// the rights of their new role.
    /// </summary>
    private async Task<BookingActor?> CurrentActorAsync(
        int organizationId,
        OrganizationCaller? caller,
        CancellationToken cancellationToken)
    {
        if (caller is null)
        {
            return null;
        }

        if (caller.OrganizationId != organizationId)
        {
            throw new ArgumentException(
                "The caller is a member of another organisation than the one being changed.",
                nameof(caller));
        }

        return BookingActor.From(
            await organizationAccess.ReauthorizeAsync(
                caller,
                OrganizationRoleRequirement.AnyMember,
                cancellationToken));
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
