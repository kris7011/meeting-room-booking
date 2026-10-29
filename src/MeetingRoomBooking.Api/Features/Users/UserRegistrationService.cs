using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Users;

public enum RegistrationOutcome
{
    /// <summary>A new user and identity were created.</summary>
    Created,

    /// <summary>The identity was already registered; nothing new was created.</summary>
    Existing,

    /// <summary>The identity belongs to a disabled user; nothing was changed.</summary>
    Disabled
}

public sealed record RegistrationResult(
    RegistrationOutcome Outcome,
    User? User);

/// <summary>
/// Registers a signed-in identity (Microsoft Entra ID or External ID) as a
/// Room Booking user: one
/// <see cref="User"/> and one <see cref="UserIdentity"/> keyed by the
/// validated issuer and object ID. Idempotent: registering again returns
/// the same user, and concurrent first registrations create exactly one.
/// </summary>
/// <remarks>
/// Registration grants nothing: the new user is a member of no organisation,
/// the legacy organisation included. An identity is never linked to an
/// existing user because their e-mail addresses match.
/// </remarks>
public sealed class UserRegistrationService(
    BookingDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<UserRegistrationService> logger)
{
    public async Task<RegistrationResult> RegisterAsync(
        SignedInIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RegisterOnceAsync(identity, cancellationToken);
        }
        catch (DbUpdateException exception)
            when (IsDuplicateIdentity(exception))
        {
            // Another writer registered the same identity first (the
            // transaction below serialises writers on one connection
            // string, so this is the fallback). Its user is the user.
            dbContext.ChangeTracker.Clear();

            return await RegisterOnceAsync(identity, cancellationToken);
        }
    }

    private async Task<RegistrationResult> RegisterOnceAsync(
        SignedInIdentity identity,
        CancellationToken cancellationToken)
    {
        var nowUtc =
            timeProvider.GetUtcNow();

        // BEGIN IMMEDIATE: the write lock is taken before the lookup, so two
        // first sign-ins of the same person cannot both see "not registered".
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var existing =
            await dbContext.UserIdentities
                .ForSignedInIdentity(identity)
                .SingleOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            var user =
                await dbContext.Users
                    .SingleAsync(
                        candidate => candidate.Id == existing.UserId,
                        cancellationToken);

            if (user.IsDisabled)
            {
                return new RegistrationResult(RegistrationOutcome.Disabled, user);
            }

            existing.RecordUse(identity.VerifiedEmail, nowUtc);
            user.RecordSeen(nowUtc);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new RegistrationResult(RegistrationOutcome.Existing, user);
        }

        var newUser =
            User.Register(
                identity.DisplayName,
                identity.VerifiedEmail,
                nowUtc);

        dbContext.Users.Add(newUser);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.UserIdentities.Add(
            UserIdentity.Create(
                newUser.Id,
                identity.Provider,
                identity.Issuer,
                identity.ObjectId,
                identity.VerifiedEmail,
                nowUtc));

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.UserRegistered,
                organizationId: null,
                actorUserId: newUser.Id,
                targetUserId: newUser.Id,
                nowUtc,
                new
                {
                    provider = identity.Provider.ToString(),
                    tenantId = identity.TenantId
                }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The public ID is what an operator needs to make this user the
        // first Owner of the legacy organisation (see LegacyOwnerBootstrap).
        logger.LogInformation(
            "Registered user {UserPublicId} for a new {Provider} identity in tenant {TenantId}.",
            newUser.PublicId,
            identity.Provider,
            identity.TenantId);

        return new RegistrationResult(RegistrationOutcome.Created, newUser);
    }

    private static bool IsDuplicateIdentity(
        DbUpdateException exception)
    {
        // SQLITE_CONSTRAINT_UNIQUE on UX_UserIdentities_Issuer_Subject.
        return exception.InnerException is SqliteException
               {
                   SqliteErrorCode: 19,
                   SqliteExtendedErrorCode: 2067
               } sqlite
               && sqlite.Message.Contains("UserIdentities.Issuer", StringComparison.Ordinal);
    }
}

public static class UserIdentityQueries
{
    /// <summary>
    /// The identity row for the validated provider, issuer and object ID, if
    /// any. The issuer alone already separates the providers; the provider is
    /// compared as well so that a row can never match a token of the other.
    /// </summary>
    public static IQueryable<UserIdentity> ForSignedInIdentity(
        this IQueryable<UserIdentity> identities,
        SignedInIdentity identity)
    {
        return identities.Where(
            candidate =>
                candidate.Provider == identity.Provider &&
                candidate.Issuer == identity.Issuer &&
                candidate.Subject == identity.ObjectId);
    }

    /// <summary>The user the signed-in identity is registered as, if any.</summary>
    public static IQueryable<User> RegisteredUser(
        this BookingDbContext dbContext,
        SignedInIdentity identity)
    {
        return dbContext.UserIdentities
            .ForSignedInIdentity(identity)
            .Join(
                dbContext.Users,
                candidate => candidate.UserId,
                user => user.Id,
                (_, user) => user);
    }
}
