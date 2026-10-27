using System.Security.Cryptography;
using System.Text;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Audit;
using MeetingRoomBooking.Api.Features.Text;
using MeetingRoomBooking.Api.Features.Users;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Organizations;

/// <summary>An organisation the caller is a member of, with their role there.</summary>
public sealed record OrganizationResponse(
    Guid PublicId,
    string Name,
    bool IsLegacy,
    string Role,
    DateTimeOffset CreatedUtc);

/// <summary><see cref="OrganizationResponse"/> plus counts for the administration page.</summary>
public sealed record OrganizationDetailsResponse(
    Guid PublicId,
    string Name,
    bool IsLegacy,
    string Role,
    DateTimeOffset CreatedUtc,
    int MemberCount,
    int ActiveRoomCount);

public sealed record CreateOrganizationRequest(
    string? Name);

public sealed record RenameOrganizationRequest(
    string? Name);

public enum OrganizationFailure
{
    None,
    NotRegistered,
    UserDisabled,
    InvalidRequest,

    /// <summary>The Idempotency-Key was used before with another request.</summary>
    IdempotencyKeyReused,

    /// <summary>The caller created too many organisations recently.</summary>
    CreationLimitReached,

    /// <summary>A replayed creation whose organisation the caller is no longer a member of.</summary>
    OrganizationNotFound,

    /// <summary>The legacy organisation's name is fixed by the migrations that created it.</summary>
    LegacyOrganizationReadOnly
}

/// <param name="Created">True only for the request that created the organisation.</param>
public sealed record OrganizationResult(
    OrganizationFailure Failure,
    OrganizationResponse? Organization,
    bool Created = false,
    IDictionary<string, string[]>? Errors = null)
{
    public static OrganizationResult Failed(OrganizationFailure failure) =>
        new(failure, null);

    public static OrganizationResult Invalid(string field, string message) =>
        new(OrganizationFailure.InvalidRequest, null, Errors: new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Creating, listing and renaming organisations. Any registered user may
/// create one and becomes its Owner; a user can be a member of several.
/// </summary>
public sealed class OrganizationService(
    BookingDbContext dbContext,
    OrganizationAccessService organizationAccess,
    TimeProvider timeProvider,
    ILogger<OrganizationService> logger)
{
    public const string IdempotencyKeyHeader = IdempotencyHeaders.Key;

    /// <summary>Per user and rolling 24 hours; repeated requests do not count.</summary>
    public const int MaximumCreatedPerDay = 10;

    public async Task<IReadOnlyList<OrganizationResponse>> ListAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        var memberships =
            await dbContext.Memberships
                .AsNoTracking()
                .Where(membership => membership.UserId == userId)
                .Join(
                    dbContext.Organizations.Where(organization => organization.DeletedUtc == null),
                    membership => membership.OrganizationId,
                    organization => organization.Id,
                    (membership, organization) =>
                        new
                        {
                            organization.PublicId,
                            organization.Name,
                            organization.IsLegacy,
                            membership.Role,
                            organization.CreatedUtc
                        })
                .ToArrayAsync(cancellationToken);

        return
        [
            .. memberships
                .OrderBy(organization => organization.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(
                    organization =>
                        new OrganizationResponse(
                            organization.PublicId,
                            organization.Name,
                            organization.IsLegacy,
                            organization.Role.ToString(),
                            organization.CreatedUtc))
        ];
    }

    public async Task<OrganizationDetailsResponse> GetDetailsAsync(
        OrganizationCaller caller,
        CancellationToken cancellationToken)
    {
        var organization =
            await dbContext.Organizations
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == caller.OrganizationId, cancellationToken);

        var memberCount =
            await dbContext.Memberships
                .CountAsync(membership => membership.OrganizationId == caller.OrganizationId, cancellationToken);

        var roomCount =
            await dbContext.MeetingRooms
                .CountAsync(
                    room => room.OrganizationId == caller.OrganizationId && room.ArchivedUtc == null,
                    cancellationToken);

        return new OrganizationDetailsResponse(
            organization.PublicId,
            organization.Name,
            organization.IsLegacy,
            caller.Role.ToString(),
            organization.CreatedUtc,
            memberCount,
            roomCount);
    }

    /// <summary>
    /// Creates an organisation with the caller as its Owner, in one
    /// transaction. Idempotent per caller and Idempotency-Key: the same key
    /// with the same name returns the organisation it created; with another
    /// name it is refused.
    /// </summary>
    public async Task<OrganizationResult> CreateAsync(
        SignedInIdentity caller,
        string? idempotencyKey,
        string? name,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out var requestId) ||
            requestId == Guid.Empty)
        {
            return OrganizationResult.Invalid(
                IdempotencyKeyHeader,
                $"Send a new random GUID in the {IdempotencyKeyHeader} header with every new organisation, and the same one when repeating a request.");
        }

        var normalizedName =
            NameText.Normalize(name, Organization.MinimumNameLength, Organization.MaximumNameLength);

        if (normalizedName is null)
        {
            return OrganizationResult.Invalid(
                "name",
                $"The name must have {Organization.MinimumNameLength} to {Organization.MaximumNameLength} characters and no control characters.");
        }

        var requestHash =
            Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalizedName)));

        try
        {
            return await CreateOnceAsync(caller, requestId, normalizedName, requestHash, cancellationToken);
        }
        catch (DbUpdateException exception)
            when (IsDuplicateCreationRequest(exception))
        {
            // A concurrent request with the same key won (the transaction
            // below serialises writers, so this is the fallback): answer
            // with what it created.
            dbContext.ChangeTracker.Clear();

            return await CreateOnceAsync(caller, requestId, normalizedName, requestHash, cancellationToken);
        }
    }

    private async Task<OrganizationResult> CreateOnceAsync(
        SignedInIdentity caller,
        Guid requestId,
        string name,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var nowUtc =
            timeProvider.GetUtcNow();

        // BEGIN IMMEDIATE: the replay lookup, the limit and the insert run
        // under the write lock.
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var user =
            await dbContext.RegisteredUser(caller).AsNoTracking().SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return OrganizationResult.Failed(OrganizationFailure.NotRegistered);
        }

        if (user.IsDisabled)
        {
            return OrganizationResult.Failed(OrganizationFailure.UserDisabled);
        }

        var existing =
            await dbContext.Organizations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    organization =>
                        organization.CreatedByUserId == user.Id &&
                        organization.CreationRequestId == requestId,
                    cancellationToken);

        if (existing is not null)
        {
            if (!string.Equals(existing.CreationRequestHash, requestHash, StringComparison.Ordinal))
            {
                return OrganizationResult.Failed(OrganizationFailure.IdempotencyKeyReused);
            }

            var role =
                await dbContext.Memberships
                    .Where(membership => membership.OrganizationId == existing.Id && membership.UserId == user.Id)
                    .Select(membership => (MembershipRole?)membership.Role)
                    .SingleOrDefaultAsync(cancellationToken);

            return role is null || existing.DeletedUtc is not null
                ? OrganizationResult.Failed(OrganizationFailure.OrganizationNotFound)
                : new OrganizationResult(OrganizationFailure.None, ToResponse(existing, role.Value));
        }

        var since = nowUtc.AddDays(-1);

        var createdRecently =
            await dbContext.Organizations
                .CountAsync(
                    organization =>
                        organization.CreatedByUserId == user.Id &&
                        organization.CreatedUtc > since,
                    cancellationToken);

        if (createdRecently >= MaximumCreatedPerDay)
        {
            return OrganizationResult.Failed(OrganizationFailure.CreationLimitReached);
        }

        var organization =
            Organization.Create(name, user.Id, requestId, requestHash, nowUtc);

        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.Memberships.Add(
            Membership.Create(
                organization.Id,
                user.Id,
                MembershipRole.Owner,
                createdByUserId: user.Id,
                nowUtc));

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.OrganizationCreated,
                organization.Id,
                actorUserId: user.Id,
                targetUserId: null,
                nowUtc,
                new { name }));

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.MembershipAdded,
                organization.Id,
                actorUserId: user.Id,
                targetUserId: user.Id,
                nowUtc,
                new { role = nameof(MembershipRole.Owner), source = "organization_created" }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "User {UserPublicId} created organisation {OrganizationPublicId}.",
            user.PublicId,
            organization.PublicId);

        return new OrganizationResult(
            OrganizationFailure.None,
            ToResponse(organization, MembershipRole.Owner),
            Created: true);
    }

    /// <summary>Owners only: the endpoint's role requirement, and again under the write lock.</summary>
    public async Task<OrganizationResult> RenameAsync(
        OrganizationCaller caller,
        string? name,
        CancellationToken cancellationToken)
    {
        var normalizedName =
            NameText.Normalize(name, Organization.MinimumNameLength, Organization.MaximumNameLength);

        if (normalizedName is null)
        {
            return OrganizationResult.Invalid(
                "name",
                $"The name must have {Organization.MinimumNameLength} to {Organization.MaximumNameLength} characters and no control characters.");
        }

        if (caller.IsLegacy)
        {
            return OrganizationResult.Failed(OrganizationFailure.LegacyOrganizationReadOnly);
        }

        var nowUtc =
            timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        caller = await organizationAccess.ReauthorizeAsync(caller, OrganizationRoleRequirement.OwnerOnly, cancellationToken);

        var organization =
            await dbContext.Organizations.SingleAsync(
                candidate => candidate.Id == caller.OrganizationId,
                cancellationToken);

        var oldName = organization.Name;

        if (!string.Equals(oldName, normalizedName, StringComparison.Ordinal))
        {
            organization.Rename(normalizedName);

            dbContext.AuditEvents.Add(
                AuditEvent.Record(
                    AuditEventType.OrganizationRenamed,
                    organization.Id,
                    caller.UserId,
                    targetUserId: null,
                    nowUtc,
                    new { oldName, newName = normalizedName }));

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new OrganizationResult(
            OrganizationFailure.None,
            ToResponse(organization, caller.Role));
    }

    private static OrganizationResponse ToResponse(
        Organization organization,
        MembershipRole role) =>
        new(
            organization.PublicId,
            organization.Name,
            organization.IsLegacy,
            role.ToString(),
            organization.CreatedUtc);

    private static bool IsDuplicateCreationRequest(
        DbUpdateException exception)
    {
        // SQLITE_CONSTRAINT_UNIQUE on UX_Organizations_CreatedByUserId_CreationRequestId.
        return exception.InnerException is SqliteException
               {
                   SqliteErrorCode: 19,
                   SqliteExtendedErrorCode: 2067
               } sqlite
               && sqlite.Message.Contains("Organizations.CreationRequestId", StringComparison.Ordinal);
    }
}
