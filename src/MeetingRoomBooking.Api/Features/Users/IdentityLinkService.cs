using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Audit;
using MeetingRoomBooking.Api.Features.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Users;

public enum IdentityLinkFailure
{
    None,
    InvalidRequest,
    NotRegistered,
    UserDisabled,

    /// <summary>No such request of the caller's user.</summary>
    LinkNotFound,

    /// <summary>Unknown code, or the request expired, was used, cancelled or claimed by another identity.</summary>
    LinkInvalid,

    /// <summary>Confirmed before a second identity claimed the code.</summary>
    NotClaimed,

    /// <summary>The claiming identity already belongs to the user who started the request.</summary>
    AlreadyLinked,

    /// <summary>
    /// The claiming identity is a Room Booking user of its own. Users are
    /// never merged: their memberships and bookings would have to move.
    /// </summary>
    BelongsToAnotherUser
}

public sealed record IdentityLinkResult<T>(
    T? Value,
    IdentityLinkFailure Failure)
{
    public static IdentityLinkResult<T> Success(T value) =>
        new(value, IdentityLinkFailure.None);

    public static IdentityLinkResult<T> Failed(IdentityLinkFailure failure) =>
        new(default, failure);
}

public sealed record StartedIdentityLink(
    Guid LinkId,
    string Code,
    DateTimeOffset ExpiresUtc);

public sealed record ClaimedIdentityLink(
    Guid LinkId,
    string AccountDisplayName,
    IdentityProvider Provider,
    DateTimeOffset ExpiresUtc);

public sealed record LinkedIdentity(
    IdentityProvider Provider,
    bool IsPersonalMicrosoftAccount,
    string? VerifiedEmail,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastUsedUtc,
    bool IsCurrent);

/// <summary>
/// Explicit linking of a second sign-in identity to an existing user (see
/// <see cref="IdentityLinkRequest"/> for the three steps). Every step runs
/// in one <c>BEGIN IMMEDIATE</c> transaction, so two claims of one code, or
/// a claim racing a registration, cannot both succeed.
/// </summary>
public sealed class IdentityLinkService(
    BookingDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<IdentityLinkService> logger)
{
    /// <summary>
    /// Step 1, by the user with an identity they already have. Cancels the
    /// user's earlier open requests: one link at a time.
    /// </summary>
    public async Task<IdentityLinkResult<StartedIdentityLink>> StartAsync(
        SignedInIdentity caller,
        CancellationToken cancellationToken)
    {
        var nowUtc =
            timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var (user, failure) =
            await ResolveUserAsync(caller, cancellationToken);

        if (user is null)
        {
            return IdentityLinkResult<StartedIdentityLink>.Failed(failure);
        }

        var open =
            await dbContext.IdentityLinkRequests
                .Where(
                    request =>
                        request.UserId == user.Id &&
                        request.CompletedUtc == null &&
                        request.CancelledUtc == null)
                .ToListAsync(cancellationToken);

        foreach (var request in open)
        {
            request.Cancel(nowUtc);
        }

        var (code, hash) =
            IdentityLinkCode.Create();

        var started =
            IdentityLinkRequest.Start(user.Id, hash, nowUtc);

        dbContext.IdentityLinkRequests.Add(started);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return IdentityLinkResult<StartedIdentityLink>.Success(
            new StartedIdentityLink(started.PublicId, code, started.ExpiresUtc));
    }

    /// <summary>
    /// Step 2, by the new identity with the code. Links nothing yet; the
    /// caller does not need to be (and must not be) a registered user.
    /// Claiming again with the same identity returns the same answer.
    /// </summary>
    public async Task<IdentityLinkResult<ClaimedIdentityLink>> ClaimAsync(
        SignedInIdentity caller,
        string? code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.Trim().Length > IdentityLinkCode.MaximumLength)
        {
            return IdentityLinkResult<ClaimedIdentityLink>.Failed(IdentityLinkFailure.InvalidRequest);
        }

        var codeHash =
            IdentityLinkCode.Hash(code.Trim());

        var nowUtc =
            timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var request =
            await dbContext.IdentityLinkRequests
                .SingleOrDefaultAsync(
                    candidate => candidate.CodeHash == codeHash,
                    cancellationToken);

        if (request is null ||
            !request.IsOpen ||
            request.IsExpired(nowUtc) ||
            (request.IsClaimed && !request.WasClaimedBy(caller)))
        {
            return IdentityLinkResult<ClaimedIdentityLink>.Failed(IdentityLinkFailure.LinkInvalid);
        }

        var account =
            await dbContext.Users
                .SingleAsync(user => user.Id == request.UserId, cancellationToken);

        if (account.IsDisabled)
        {
            return IdentityLinkResult<ClaimedIdentityLink>.Failed(IdentityLinkFailure.LinkInvalid);
        }

        var existing =
            await dbContext.UserIdentities
                .ForSignedInIdentity(caller)
                .Select(identity => (int?)identity.UserId)
                .SingleOrDefaultAsync(cancellationToken);

        if (existing is { } ownerId)
        {
            return IdentityLinkResult<ClaimedIdentityLink>.Failed(
                ownerId == request.UserId
                    ? IdentityLinkFailure.AlreadyLinked
                    : IdentityLinkFailure.BelongsToAnotherUser);
        }

        if (!request.IsClaimed)
        {
            request.Claim(caller, nowUtc);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return IdentityLinkResult<ClaimedIdentityLink>.Success(
            new ClaimedIdentityLink(request.PublicId, account.DisplayName, caller.Provider, request.ExpiresUtc));
    }

    /// <summary>
    /// Step 3, by the user who started the request, signed in with one of
    /// their identities. Creates the new identity. A repeat after success
    /// returns the same result.
    /// </summary>
    public async Task<IdentityLinkResult<IReadOnlyList<LinkedIdentity>>> ConfirmAsync(
        SignedInIdentity caller,
        Guid linkId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ConfirmOnceAsync(caller, linkId, cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 })
        {
            // The claimed identity was registered by someone else between the
            // check and the insert (UX_UserIdentities_Issuer_Subject).
            dbContext.ChangeTracker.Clear();
            return IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Failed(IdentityLinkFailure.BelongsToAnotherUser);
        }
    }

    private async Task<IdentityLinkResult<IReadOnlyList<LinkedIdentity>>> ConfirmOnceAsync(
        SignedInIdentity caller,
        Guid linkId,
        CancellationToken cancellationToken)
    {
        var nowUtc =
            timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var (user, failure) =
            await ResolveUserAsync(caller, cancellationToken);

        if (user is null)
        {
            return IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Failed(failure);
        }

        // Another user's request looks like no request at all.
        var request =
            await dbContext.IdentityLinkRequests
                .SingleOrDefaultAsync(
                    candidate => candidate.PublicId == linkId && candidate.UserId == user.Id,
                    cancellationToken);

        if (request is null)
        {
            return IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Failed(IdentityLinkFailure.LinkNotFound);
        }

        if (request.CompletedUtc is not null)
        {
            return IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Success(
                await ListAsync(user.Id, caller, cancellationToken));
        }

        if (!request.IsOpen || request.IsExpired(nowUtc))
        {
            return IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Failed(IdentityLinkFailure.LinkInvalid);
        }

        if (!request.IsClaimed)
        {
            return IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Failed(IdentityLinkFailure.NotClaimed);
        }

        var claimed =
            new SignedInIdentity(
                request.ClaimedProvider!.Value,
                request.ClaimedIssuer!,
                request.ClaimedSubject!,
                TenantId: string.Empty,
                DisplayName: string.Empty,
                Username: null,
                request.ClaimedVerifiedEmail);

        // Registered since the claim: it is a user of its own now.
        if (await dbContext.UserIdentities.ForSignedInIdentity(claimed).AnyAsync(cancellationToken))
        {
            return IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Failed(IdentityLinkFailure.BelongsToAnotherUser);
        }

        dbContext.UserIdentities.Add(
            UserIdentity.Create(
                user.Id,
                claimed.Provider,
                claimed.Issuer,
                claimed.ObjectId,
                claimed.VerifiedEmail,
                nowUtc));

        request.Complete(nowUtc);

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.IdentityLinked,
                organizationId: null,
                actorUserId: user.Id,
                targetUserId: user.Id,
                nowUtc,
                new
                {
                    provider = claimed.Provider.ToString(),
                    confirmedWith = caller.Provider.ToString()
                }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Linked a new {Provider} identity to user {UserPublicId}.",
            claimed.Provider,
            user.PublicId);

        return IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Success(
            await ListAsync(user.Id, caller, cancellationToken));
    }

    /// <summary>Cancels an open request of the caller's user; nothing happens to a finished one.</summary>
    public async Task<IdentityLinkFailure> CancelAsync(
        SignedInIdentity caller,
        Guid linkId,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var (user, failure) =
            await ResolveUserAsync(caller, cancellationToken);

        if (user is null)
        {
            return failure;
        }

        var request =
            await dbContext.IdentityLinkRequests
                .SingleOrDefaultAsync(
                    candidate => candidate.PublicId == linkId && candidate.UserId == user.Id,
                    cancellationToken);

        if (request is null)
        {
            return IdentityLinkFailure.LinkNotFound;
        }

        request.Cancel(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return IdentityLinkFailure.None;
    }

    /// <summary>The ways the caller's user can sign in.</summary>
    public async Task<IdentityLinkResult<IReadOnlyList<LinkedIdentity>>> ListIdentitiesAsync(
        SignedInIdentity caller,
        CancellationToken cancellationToken)
    {
        var (user, failure) =
            await ResolveUserAsync(caller, cancellationToken);

        return user is null
            ? IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Failed(failure)
            : IdentityLinkResult<IReadOnlyList<LinkedIdentity>>.Success(
                await ListAsync(user.Id, caller, cancellationToken));
    }

    private async Task<(User? User, IdentityLinkFailure Failure)> ResolveUserAsync(
        SignedInIdentity caller,
        CancellationToken cancellationToken)
    {
        var user =
            await dbContext.RegisteredUser(caller).SingleOrDefaultAsync(cancellationToken);

        return user switch
        {
            null => (null, IdentityLinkFailure.NotRegistered),
            { IsDisabled: true } => (null, IdentityLinkFailure.UserDisabled),
            _ => (user, IdentityLinkFailure.None)
        };
    }

    private async Task<IReadOnlyList<LinkedIdentity>> ListAsync(
        int userId,
        SignedInIdentity caller,
        CancellationToken cancellationToken)
    {
        var personalIssuer =
            $"/{EntraAuthentication.PersonalAccountsTenantId}/";

        var identities =
            await dbContext.UserIdentities
                .AsNoTracking()
                .Where(identity => identity.UserId == userId)
                .ToListAsync(cancellationToken);

        return
        [
            .. identities
                .OrderBy(identity => identity.CreatedUtc)
                .Select(
                    identity =>
                        new LinkedIdentity(
                            identity.Provider,
                            identity.Provider == IdentityProvider.Entra &&
                            identity.Issuer.Contains(personalIssuer, StringComparison.Ordinal),
                            identity.VerifiedEmail,
                            identity.CreatedUtc,
                            identity.LastUsedUtc,
                            identity.Provider == caller.Provider &&
                            identity.Issuer == caller.Issuer &&
                            identity.Subject == caller.ObjectId))
        ];
    }
}
