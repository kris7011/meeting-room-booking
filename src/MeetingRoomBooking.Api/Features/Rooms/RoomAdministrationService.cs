using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Audit;
using MeetingRoomBooking.Api.Features.Organizations;
using MeetingRoomBooking.Api.Features.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Rooms;

public sealed record SaveRoomRequest(
    string? Name,
    int? Capacity);

public enum RoomFailure
{
    None,
    InvalidRequest,
    RoomNotFound,

    /// <summary>Another active room of the organisation has this name.</summary>
    NameTaken,

    /// <summary>Archived rooms are restored before they are changed.</summary>
    RoomArchived,

    /// <summary>The room still has bookings that have not ended.</summary>
    HasUpcomingBookings
}

public sealed record RoomResult(
    RoomFailure Failure,
    MeetingRoom? Room,
    IDictionary<string, string[]>? Errors = null,
    int UpcomingBookings = 0)
{
    public static RoomResult Success(MeetingRoom room) =>
        new(RoomFailure.None, room);

    public static RoomResult Failed(RoomFailure failure) =>
        new(failure, null);
}

/// <summary>
/// Creating, changing, archiving and restoring an organisation's rooms.
/// Owners and Admins only: the endpoints' role requirement, and again under
/// the write lock of each change. Rooms are never deleted: archiving hides a
/// room from searches and new bookings and keeps its bookings.
/// </summary>
public sealed class RoomAdministrationService(
    BookingDbContext dbContext,
    OrganizationAccessService organizationAccess,
    TimeProvider timeProvider)
{
    public async Task<RoomResult> CreateAsync(
        OrganizationCaller caller,
        SaveRoomRequest request,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var name = Name(request);
        var nowUtc = timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        caller = await organizationAccess.ReauthorizeAsync(caller, OrganizationRoleRequirement.OwnerOrAdmin, cancellationToken);

        if (await NameTakenAsync(caller.OrganizationId, name, exceptRoomId: null, cancellationToken))
        {
            return RoomResult.Failed(RoomFailure.NameTaken);
        }

        var room =
            MeetingRoom.Create(caller.OrganizationId, name, request.Capacity!.Value);

        dbContext.MeetingRooms.Add(room);

        // The audit event is built after saving: the new room's ID only
        // exists then.
        return await SaveAsync(
            transaction,
            room,
            () => AuditEvent.Record(
                AuditEventType.RoomCreated,
                caller.OrganizationId,
                caller.UserId,
                targetUserId: null,
                nowUtc,
                new { roomId = room.Id, name, capacity = room.Capacity }),
            cancellationToken);
    }

    public async Task<RoomResult> UpdateAsync(
        OrganizationCaller caller,
        int roomId,
        SaveRoomRequest request,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var name = Name(request);
        var nowUtc = timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        caller = await organizationAccess.ReauthorizeAsync(caller, OrganizationRoleRequirement.OwnerOrAdmin, cancellationToken);

        var room =
            await FindAsync(caller.OrganizationId, roomId, cancellationToken);

        if (room is null)
        {
            return RoomResult.Failed(RoomFailure.RoomNotFound);
        }

        if (room.IsArchived)
        {
            return RoomResult.Failed(RoomFailure.RoomArchived);
        }

        if (await NameTakenAsync(caller.OrganizationId, name, exceptRoomId: room.Id, cancellationToken))
        {
            return RoomResult.Failed(RoomFailure.NameTaken);
        }

        var old = new { name = room.Name, capacity = room.Capacity };

        room.Update(name, request.Capacity!.Value);

        return await SaveAsync(
            transaction,
            room,
            () => AuditEvent.Record(
                AuditEventType.RoomUpdated,
                caller.OrganizationId,
                caller.UserId,
                targetUserId: null,
                nowUtc,
                new { roomId, oldName = old.name, newName = name, oldCapacity = old.capacity, newCapacity = room.Capacity }),
            cancellationToken);
    }

    /// <summary>
    /// Refused while the room has bookings that have not ended (they would
    /// silently lose their room); past bookings are kept as they are.
    /// Archiving an archived room changes nothing.
    /// </summary>
    public async Task<RoomResult> ArchiveAsync(
        OrganizationCaller caller,
        int roomId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        caller = await organizationAccess.ReauthorizeAsync(caller, OrganizationRoleRequirement.OwnerOrAdmin, cancellationToken);

        var room =
            await FindAsync(caller.OrganizationId, roomId, cancellationToken);

        if (room is null)
        {
            return RoomResult.Failed(RoomFailure.RoomNotFound);
        }

        if (room.IsArchived)
        {
            return RoomResult.Success(room);
        }

        var upcoming =
            await dbContext.Bookings
                .CountAsync(
                    booking => booking.RoomId == room.Id && booking.EndUtc > nowUtc,
                    cancellationToken);

        if (upcoming > 0)
        {
            return new RoomResult(RoomFailure.HasUpcomingBookings, null, UpcomingBookings: upcoming);
        }

        room.Archive(nowUtc);

        return await SaveAsync(
            transaction,
            room,
            () => AuditEvent.Record(
                AuditEventType.RoomArchived,
                caller.OrganizationId,
                caller.UserId,
                targetUserId: null,
                nowUtc,
                new { roomId, name = room.Name }),
            cancellationToken);
    }

    public async Task<RoomResult> RestoreAsync(
        OrganizationCaller caller,
        int roomId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        caller = await organizationAccess.ReauthorizeAsync(caller, OrganizationRoleRequirement.OwnerOrAdmin, cancellationToken);

        var room =
            await FindAsync(caller.OrganizationId, roomId, cancellationToken);

        if (room is null)
        {
            return RoomResult.Failed(RoomFailure.RoomNotFound);
        }

        if (!room.IsArchived)
        {
            return RoomResult.Success(room);
        }

        if (await NameTakenAsync(caller.OrganizationId, room.Name, exceptRoomId: room.Id, cancellationToken))
        {
            return RoomResult.Failed(RoomFailure.NameTaken);
        }

        room.Restore();

        return await SaveAsync(
            transaction,
            room,
            () => AuditEvent.Record(
                AuditEventType.RoomRestored,
                caller.OrganizationId,
                caller.UserId,
                targetUserId: null,
                nowUtc,
                new { roomId, name = room.Name }),
            cancellationToken);
    }

    private static RoomResult? Validate(
        SaveRoomRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (NameText.Normalize(request.Name, 1, MeetingRoom.MaximumNameLength) is null)
        {
            errors["name"] = [$"The name must have 1 to {MeetingRoom.MaximumNameLength} characters and no control characters."];
        }

        if (request.Capacity is not (>= 1 and <= MeetingRoom.MaximumCapacity))
        {
            errors["capacity"] = [$"The capacity must be a whole number from 1 to {MeetingRoom.MaximumCapacity}."];
        }

        return errors.Count == 0
            ? null
            : new RoomResult(RoomFailure.InvalidRequest, null, errors);
    }

    private static string Name(
        SaveRoomRequest request) =>
        NameText.Normalize(request.Name, 1, MeetingRoom.MaximumNameLength)!;

    private Task<MeetingRoom?> FindAsync(
        int organizationId,
        int roomId,
        CancellationToken cancellationToken) =>
        dbContext.MeetingRooms.SingleOrDefaultAsync(
            room => room.Id == roomId && room.OrganizationId == organizationId,
            cancellationToken);

    // The unique index is case-sensitive; people are not ("board room" and
    // "Board Room" are the same room to them).
    private Task<bool> NameTakenAsync(
        int organizationId,
        string name,
        int? exceptRoomId,
        CancellationToken cancellationToken)
    {
        var lowerName = name.ToLowerInvariant();

        return dbContext.MeetingRooms.AnyAsync(
            room =>
                room.OrganizationId == organizationId &&
                room.ArchivedUtc == null &&
                room.Id != exceptRoomId &&
                room.Name.ToLower() == lowerName,
            cancellationToken);
    }

    private async Task<RoomResult> SaveAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        MeetingRoom room,
        Func<AuditEvent> auditEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite &&
                  sqlite.Message.Contains("MeetingRooms.Name", StringComparison.Ordinal))
        {
            return RoomResult.Failed(RoomFailure.NameTaken);
        }

        dbContext.AuditEvents.Add(auditEvent());
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RoomResult.Success(room);
    }
}
