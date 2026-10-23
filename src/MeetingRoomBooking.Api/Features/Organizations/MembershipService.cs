using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Organizations;

/// <param name="UserPublicId">The member's public user ID; the key for changing or removing them.</param>
/// <param name="IsCurrentUser">The caller.</param>
public sealed record MemberResponse(
    Guid UserPublicId,
    string DisplayName,
    string Role,
    DateTimeOffset JoinedUtc,
    bool IsCurrentUser);

public sealed record ChangeRoleRequest(
    string? Role);

public enum MembershipFailure
{
    None,
    InvalidRequest,
    MemberNotFound,

    /// <summary>Only Owners change roles and remove others; anyone may leave.</summary>
    PermissionDenied,

    /// <summary>The change would leave the organisation without an Owner.</summary>
    LastOwner
}

public sealed record MembershipResult(
    MembershipFailure Failure,
    MemberResponse? Member = null,
    IDictionary<string, string[]>? Errors = null)
{
    public static MembershipResult Failed(MembershipFailure failure) =>
        new(failure);
}

/// <summary>
/// Members of an organisation and their roles.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Owners and Admins see the member list.</item>
/// <item>Only Owners change roles, including making someone else an Owner (ownership is handed over this way, never by invitation). An Admin can never make anyone, themselves included, an Owner.</item>
/// <item>Owners remove members; every member may leave by removing themselves.</item>
/// <item>The last Owner can be neither demoted nor removed, nor leave: promote someone else first. The database enforces the same rule (TR_Memberships_LastOwner_*).</item>
/// </list>
/// </remarks>
public sealed class MembershipService(
    BookingDbContext dbContext,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<MemberResponse>> ListAsync(
        OrganizationCaller caller,
        CancellationToken cancellationToken)
    {
        var members =
            await dbContext.Memberships
                .AsNoTracking()
                .Where(membership => membership.OrganizationId == caller.OrganizationId)
                .Join(
                    dbContext.Users,
                    membership => membership.UserId,
                    user => user.Id,
                    (membership, user) =>
                        new
                        {
                            user.Id,
                            user.PublicId,
                            user.DisplayName,
                            membership.Role,
                            membership.CreatedUtc
                        })
                .ToArrayAsync(cancellationToken);

        return
        [
            .. members
                .OrderBy(member => member.Role)
                .ThenBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(
                    member =>
                        new MemberResponse(
                            member.PublicId,
                            member.DisplayName,
                            member.Role.ToString(),
                            member.CreatedUtc,
                            member.Id == caller.UserId))
        ];
    }

    public async Task<MembershipResult> ChangeRoleAsync(
        OrganizationCaller caller,
        Guid userPublicId,
        string? role,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<MembershipRole>(role, ignoreCase: true, out var newRole) ||
            !Enum.IsDefined(newRole) ||
            int.TryParse(role, out _))
        {
            return new MembershipResult(
                MembershipFailure.InvalidRequest,
                Errors: new Dictionary<string, string[]> { ["role"] = ["The role must be Owner, Admin or Member."] });
        }

        var nowUtc = timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Read again under the write lock: a concurrent request may have
        // demoted or removed the caller since the access filter ran.
        if (await CurrentRoleAsync(caller, cancellationToken) != MembershipRole.Owner)
        {
            return MembershipResult.Failed(MembershipFailure.PermissionDenied);
        }

        var target = await FindAsync(caller.OrganizationId, userPublicId, cancellationToken);

        if (target is null)
        {
            return MembershipResult.Failed(MembershipFailure.MemberNotFound);
        }

        var (membership, user) = target.Value;
        var oldRole = membership.Role;

        if (oldRole != newRole)
        {
            if (oldRole == MembershipRole.Owner &&
                await IsLastOwnerAsync(caller.OrganizationId, membership.UserId, cancellationToken))
            {
                return MembershipResult.Failed(MembershipFailure.LastOwner);
            }

            membership.ChangeRole(newRole, nowUtc);

            dbContext.AuditEvents.Add(
                AuditEvent.Record(
                    AuditEventType.MembershipRoleChanged,
                    caller.OrganizationId,
                    caller.UserId,
                    targetUserId: membership.UserId,
                    nowUtc,
                    new { oldRole = oldRole.ToString(), newRole = newRole.ToString() }));

            if (await TrySaveAsync(cancellationToken) is { } failure)
            {
                return failure;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return new MembershipResult(
            MembershipFailure.None,
            new MemberResponse(
                user.PublicId,
                user.DisplayName,
                membership.Role.ToString(),
                membership.CreatedUtc,
                membership.UserId == caller.UserId));
    }

    public async Task<MembershipResult> RemoveAsync(
        OrganizationCaller caller,
        Guid userPublicId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var target = await FindAsync(caller.OrganizationId, userPublicId, cancellationToken);

        if (target is null)
        {
            return MembershipResult.Failed(MembershipFailure.MemberNotFound);
        }

        var (membership, _) = target.Value;
        var leaving = membership.UserId == caller.UserId;

        if (!leaving &&
            await CurrentRoleAsync(caller, cancellationToken) != MembershipRole.Owner)
        {
            return MembershipResult.Failed(MembershipFailure.PermissionDenied);
        }

        if (membership.Role == MembershipRole.Owner &&
            await IsLastOwnerAsync(caller.OrganizationId, membership.UserId, cancellationToken))
        {
            return MembershipResult.Failed(MembershipFailure.LastOwner);
        }

        dbContext.Memberships.Remove(membership);

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.MembershipRemoved,
                caller.OrganizationId,
                caller.UserId,
                targetUserId: membership.UserId,
                nowUtc,
                new { role = membership.Role.ToString(), reason = leaving ? "left" : "removed" }));

        if (await TrySaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        await transaction.CommitAsync(cancellationToken);

        return new MembershipResult(MembershipFailure.None);
    }

    private async Task<(Membership Membership, Users.User User)?> FindAsync(
        int organizationId,
        Guid userPublicId,
        CancellationToken cancellationToken)
    {
        var user =
            await dbContext.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.PublicId == userPublicId, cancellationToken);

        if (user is null)
        {
            return null;
        }

        var membership =
            await dbContext.Memberships
                .SingleOrDefaultAsync(
                    candidate => candidate.OrganizationId == organizationId && candidate.UserId == user.Id,
                    cancellationToken);

        return membership is null ? null : (membership, user);
    }

    private Task<MembershipRole?> CurrentRoleAsync(
        OrganizationCaller caller,
        CancellationToken cancellationToken) =>
        dbContext.Memberships
            .AsNoTracking()
            .Where(membership => membership.OrganizationId == caller.OrganizationId && membership.UserId == caller.UserId)
            .Select(membership => (MembershipRole?)membership.Role)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<bool> IsLastOwnerAsync(
        int organizationId,
        int userId,
        CancellationToken cancellationToken) =>
        !await dbContext.Memberships
            .AnyAsync(
                membership =>
                    membership.OrganizationId == organizationId &&
                    membership.Role == MembershipRole.Owner &&
                    membership.UserId != userId,
                cancellationToken);

    // The last-Owner triggers are the backstop for a check above that a
    // concurrent change made stale.
    private async Task<MembershipResult?> TrySaveAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite &&
                  sqlite.Message.Contains("last_owner", StringComparison.Ordinal))
        {
            return MembershipResult.Failed(MembershipFailure.LastOwner);
        }
    }
}
