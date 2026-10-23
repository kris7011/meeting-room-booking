using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Audit;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Organizations;

public enum LegacyOwnerBootstrapOutcome
{
    /// <summary>No user is configured.</summary>
    NotConfigured,

    /// <summary>The configured user was made the first Owner.</summary>
    OwnerAdded,

    /// <summary>The configured user already is an Owner.</summary>
    AlreadyOwner,

    /// <summary>Someone else already owns the organisation; nothing changed.</summary>
    OwnedByAnotherUser,

    /// <summary>No user has the configured public ID (not registered yet).</summary>
    UserNotFound,

    /// <summary>The configured user is disabled; nothing changed.</summary>
    UserDisabled
}

/// <summary>
/// Makes one registered user the first Owner of the legacy organisation,
/// which was created by a migration and so has no creator to own it.
/// </summary>
/// <remarks>
/// <para>
/// Only someone who controls the API's configuration can do this: the user
/// is named by <c>Organizations:InitialLegacyOwnerUserId</c>, the public ID
/// the API generated when they registered (logged at registration and shown
/// in the app). Nothing a client sends, and no e-mail address, is involved.
/// </para>
/// <para>
/// It runs once at start-up and only while the legacy organisation has no
/// Owner. Once it has one, the setting changes nothing; further members are
/// added through invitations by its Owners and Admins.
/// </para>
/// </remarks>
public static class LegacyOwnerBootstrap
{
    public const string SettingName =
        "Organizations:InitialLegacyOwnerUserId";

    public static async Task<LegacyOwnerBootstrapOutcome> ApplyAsync(
        BookingDbContext dbContext,
        string? configuredUserId,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuredUserId))
        {
            return LegacyOwnerBootstrapOutcome.NotConfigured;
        }

        if (!Guid.TryParse(configuredUserId, out var userPublicId))
        {
            throw new InvalidOperationException(
                $"{SettingName} must be the public ID (a GUID) of a registered user.");
        }

        var nowUtc =
            timeProvider.GetUtcNow();

        // BEGIN IMMEDIATE: "has no Owner" and adding one happen under the
        // write lock, so two API instances cannot both add a first Owner.
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var user =
            await dbContext.Users
                .SingleOrDefaultAsync(
                    candidate => candidate.PublicId == userPublicId,
                    cancellationToken);

        if (user is null)
        {
            logger.LogWarning(
                "{Setting} names user {UserPublicId}, who has not registered. Sign in with the app first, then restart the API.",
                SettingName,
                userPublicId);

            return LegacyOwnerBootstrapOutcome.UserNotFound;
        }

        if (user.IsDisabled)
        {
            logger.LogWarning(
                "{Setting} names user {UserPublicId}, whose account is disabled. No Owner was added.",
                SettingName,
                userPublicId);

            return LegacyOwnerBootstrapOutcome.UserDisabled;
        }

        var owners =
            await dbContext.Memberships
                .Where(
                    membership =>
                        membership.OrganizationId == LegacyOrganization.Id &&
                        membership.Role == MembershipRole.Owner)
                .Select(membership => membership.UserId)
                .ToArrayAsync(cancellationToken);

        if (owners.Contains(user.Id))
        {
            return LegacyOwnerBootstrapOutcome.AlreadyOwner;
        }

        if (owners.Length > 0)
        {
            logger.LogInformation(
                "The legacy organisation already has an Owner, so {Setting} was ignored. Owners add members through invitations.",
                SettingName);

            return LegacyOwnerBootstrapOutcome.OwnedByAnotherUser;
        }

        var membership =
            await dbContext.Memberships
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.OrganizationId == LegacyOrganization.Id &&
                        candidate.UserId == user.Id,
                    cancellationToken);

        if (membership is null)
        {
            dbContext.Memberships.Add(
                Membership.Create(
                    LegacyOrganization.Id,
                    user.Id,
                    MembershipRole.Owner,
                    createdByUserId: null,
                    nowUtc));

            dbContext.AuditEvents.Add(
                AuditEvent.Record(
                    AuditEventType.MembershipAdded,
                    LegacyOrganization.Id,
                    actorUserId: null,
                    targetUserId: user.Id,
                    nowUtc,
                    new { role = nameof(MembershipRole.Owner), source = SettingName }));
        }
        else
        {
            var previousRole =
                membership.Role;

            membership.ChangeRole(MembershipRole.Owner, nowUtc);

            dbContext.AuditEvents.Add(
                AuditEvent.Record(
                    AuditEventType.MembershipRoleChanged,
                    LegacyOrganization.Id,
                    actorUserId: null,
                    targetUserId: user.Id,
                    nowUtc,
                    new
                    {
                        oldRole = previousRole.ToString(),
                        newRole = nameof(MembershipRole.Owner),
                        source = SettingName
                    }));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "User {UserPublicId} is now the first Owner of the legacy organisation ({Setting}).",
            userPublicId,
            SettingName);

        return LegacyOwnerBootstrapOutcome.OwnerAdded;
    }

    public static async Task ApplyLegacyOwnerBootstrapAsync(
        this WebApplication application)
    {
        await using var scope =
            application.Services.CreateAsyncScope();

        await ApplyAsync(
            scope.ServiceProvider.GetRequiredService<BookingDbContext>(),
            application.Configuration[SettingName],
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(LegacyOwnerBootstrap)));
    }
}
