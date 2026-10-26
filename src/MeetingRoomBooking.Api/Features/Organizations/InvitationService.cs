using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Audit;
using MeetingRoomBooking.Api.Features.Users;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Organizations;

public enum InvitationFailure
{
    None,
    NotRegistered,
    UserDisabled,
    InvalidRequest,

    /// <summary>No such organisation, or the caller is not a member of it.</summary>
    OrganizationNotFound,

    /// <summary>The caller is a member, but their role may not do this.</summary>
    PermissionDenied,

    InvitationNotFound,
    InvitationExpired,

    /// <summary>Revoked, accepted by someone else, or its inviter has lost the right to invite.</summary>
    InvitationNoLongerValid,

    /// <summary>The caller's token carries no e-mail address Microsoft has verified.</summary>
    VerifiedEmailRequired,

    /// <summary>The caller's verified e-mail is not the address the invitation was sent to.</summary>
    EmailMismatch,

    AlreadyMember
}

public sealed record CreatedInvitation(
    Guid OrganizationPublicId,
    string OrganizationName,
    string Email,
    MembershipRole Role,
    DateTimeOffset ExpiresUtc,
    string Token);

public sealed record AcceptedInvitation(
    Guid OrganizationPublicId,
    string OrganizationName,
    bool IsLegacy,
    MembershipRole Role);

public sealed record InvitationResult<T>(
    InvitationFailure Failure,
    T? Value,
    IDictionary<string, string[]>? Errors = null)
    where T : class
{
    public static InvitationResult<T> Success(T value) =>
        new(InvitationFailure.None, value);

    public static InvitationResult<T> Failed(InvitationFailure failure) =>
        new(failure, null);

    public static InvitationResult<T> Invalid(string field, string message) =>
        new(InvitationFailure.InvalidRequest, null, new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// The only way, besides the configured first Owner of the legacy
/// organisation, that anyone gets access to an organisation.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only Owners and Admins of an organisation may invite, and only Owners may invite Admins. No invitation grants Owner.</item>
/// <item>Accepting needs the secret token from the invitation link and a signed-in, registered user whose token carries a verified e-mail equal to the invited address. Neither alone is enough: an e-mail address never grants access by itself.</item>
/// <item>The inviter must still be allowed to invite when the invitation is accepted.</item>
/// </list>
/// </remarks>
public sealed class InvitationService(
    BookingDbContext dbContext,
    OrganizationAccessService organizationAccess,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime =
        TimeSpan.FromDays(7);

    /// <summary>Generated tokens are 43 characters; anything far longer is not one.</summary>
    private const int MaximumTokenLength = 128;

    public async Task<InvitationResult<CreatedInvitation>> CreateAsync(
        SignedInIdentity caller,
        Guid organizationPublicId,
        string? email,
        string? role,
        CancellationToken cancellationToken)
    {
        var normalizedEmail =
            EmailAddress.Normalize(email);

        if (normalizedEmail is null)
        {
            return InvitationResult<CreatedInvitation>.Invalid(
                "email",
                "A valid e-mail address is required.");
        }

        if (!Enum.TryParse<MembershipRole>(role, ignoreCase: true, out var invitedRole) ||
            !Enum.IsDefined(invitedRole) ||
            invitedRole == MembershipRole.Owner ||
            int.TryParse(role, out _))
        {
            return InvitationResult<CreatedInvitation>.Invalid(
                "role",
                "The role must be Admin or Member. Ownership is never granted by invitation.");
        }

        var nowUtc =
            timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var user =
            await dbContext.RegisteredUser(caller).SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return InvitationResult<CreatedInvitation>.Failed(InvitationFailure.NotRegistered);
        }

        if (user.IsDisabled)
        {
            return InvitationResult<CreatedInvitation>.Failed(InvitationFailure.UserDisabled);
        }

        var organization =
            await dbContext.Organizations
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.PublicId == organizationPublicId &&
                        candidate.DeletedUtc == null,
                    cancellationToken);

        var callerRole =
            organization is null
                ? null
                : await RoleOfAsync(organization.Id, user.Id, cancellationToken);

        // Not a member looks exactly like "no such organisation".
        if (organization is null || callerRole is null)
        {
            return InvitationResult<CreatedInvitation>.Failed(InvitationFailure.OrganizationNotFound);
        }

        if (!MayInvite(callerRole.Value, invitedRole))
        {
            return InvitationResult<CreatedInvitation>.Failed(InvitationFailure.PermissionDenied);
        }

        // One open invitation per address and organisation: inviting again
        // replaces the old one (also when it has expired).
        var open =
            await dbContext.Invitations
                .Where(
                    invitation =>
                        invitation.OrganizationId == organization.Id &&
                        invitation.Email == normalizedEmail &&
                        invitation.AcceptedUtc == null &&
                        invitation.RevokedUtc == null)
                .ToArrayAsync(cancellationToken);

        foreach (var invitation in open)
        {
            invitation.Revoke(nowUtc);

            dbContext.AuditEvents.Add(
                AuditEvent.Record(
                    AuditEventType.InvitationRevoked,
                    organization.Id,
                    user.Id,
                    targetUserId: null,
                    nowUtc,
                    new { email = normalizedEmail, reason = "replaced" }));
        }

        // Saved first: the open-invitation index must be free before the
        // new invitation is inserted.
        await dbContext.SaveChangesAsync(cancellationToken);

        var (token, tokenHash) =
            InvitationToken.Create();

        var created =
            Invitation.Create(
                organization.Id,
                normalizedEmail,
                invitedRole,
                tokenHash,
                user.Id,
                nowUtc,
                Lifetime);

        dbContext.Invitations.Add(created);

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.InvitationCreated,
                organization.Id,
                user.Id,
                targetUserId: null,
                nowUtc,
                new { email = normalizedEmail, role = invitedRole.ToString() }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InvitationResult<CreatedInvitation>.Success(
            new CreatedInvitation(
                organization.PublicId,
                organization.Name,
                normalizedEmail,
                invitedRole,
                created.ExpiresUtc,
                token));
    }

    public async Task<InvitationResult<AcceptedInvitation>> AcceptAsync(
        SignedInIdentity caller,
        string? token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.Length > MaximumTokenLength)
        {
            return InvitationResult<AcceptedInvitation>.Invalid(
                "token",
                "The invitation token is missing or malformed.");
        }

        var tokenHash =
            InvitationToken.Hash(token.Trim());

        var nowUtc =
            timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var user =
            await dbContext.RegisteredUser(caller).SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.NotRegistered);
        }

        if (user.IsDisabled)
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.UserDisabled);
        }

        var invitation =
            await dbContext.Invitations
                .SingleOrDefaultAsync(
                    candidate => candidate.TokenHash == tokenHash,
                    cancellationToken);

        var organization =
            invitation is null
                ? null
                : await dbContext.Organizations
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate =>
                            candidate.Id == invitation.OrganizationId &&
                            candidate.DeletedUtc == null,
                        cancellationToken);

        if (invitation is null || organization is null)
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.InvitationNotFound);
        }

        // Accepting twice (a retry, or two taps) returns the same membership.
        if (invitation.AcceptedByUserId == user.Id)
        {
            var currentRole =
                await RoleOfAsync(organization.Id, user.Id, cancellationToken);

            return currentRole is null
                ? InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.InvitationNoLongerValid)
                : InvitationResult<AcceptedInvitation>.Success(
                    new AcceptedInvitation(organization.PublicId, organization.Name, organization.IsLegacy, currentRole.Value));
        }

        if (!invitation.IsOpen)
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.InvitationNoLongerValid);
        }

        if (invitation.IsExpired(nowUtc))
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.InvitationExpired);
        }

        // The token proves the link reached someone; the verified e-mail
        // proves it is the person it was sent to. A forwarded or leaked link
        // is useless to anyone else.
        if (caller.VerifiedEmail is null)
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.VerifiedEmailRequired);
        }

        if (!string.Equals(caller.VerifiedEmail, invitation.Email, StringComparison.Ordinal))
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.EmailMismatch);
        }

        if (!await InviterMayStillInviteAsync(invitation, cancellationToken))
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.InvitationNoLongerValid);
        }

        if (await RoleOfAsync(organization.Id, user.Id, cancellationToken) is not null)
        {
            return InvitationResult<AcceptedInvitation>.Failed(InvitationFailure.AlreadyMember);
        }

        dbContext.Memberships.Add(
            Membership.Create(
                organization.Id,
                user.Id,
                invitation.Role,
                invitation.InvitedByUserId,
                nowUtc));

        invitation.Accept(user.Id, nowUtc);

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.InvitationAccepted,
                organization.Id,
                user.Id,
                targetUserId: user.Id,
                nowUtc,
                new { role = invitation.Role.ToString() }));

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.MembershipAdded,
                organization.Id,
                actorUserId: invitation.InvitedByUserId,
                targetUserId: user.Id,
                nowUtc,
                new { role = invitation.Role.ToString(), source = "invitation" }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InvitationResult<AcceptedInvitation>.Success(
            new AcceptedInvitation(organization.PublicId, organization.Name, organization.IsLegacy, invitation.Role));
    }

    /// <summary>
    /// The organisation's open invitations (neither accepted nor revoked),
    /// expired ones included so they can be revoked or sent again. Never the
    /// token or its hash.
    /// </summary>
    public async Task<IReadOnlyList<InvitationResponse>> ListOpenAsync(
        OrganizationCaller caller,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        var open =
            await dbContext.Invitations
                .AsNoTracking()
                .Where(
                    invitation =>
                        invitation.OrganizationId == caller.OrganizationId &&
                        invitation.AcceptedUtc == null &&
                        invitation.RevokedUtc == null)
                .Join(
                    dbContext.Users,
                    invitation => invitation.InvitedByUserId,
                    user => user.Id,
                    (invitation, user) => new { Invitation = invitation, InvitedBy = user.DisplayName })
                .ToArrayAsync(cancellationToken);

        return
        [
            .. open
                .OrderByDescending(row => row.Invitation.CreatedUtc)
                .Select(
                    row =>
                        new InvitationResponse(
                            row.Invitation.Id,
                            row.Invitation.Email,
                            row.Invitation.Role.ToString(),
                            row.Invitation.CreatedUtc,
                            row.Invitation.ExpiresUtc,
                            row.Invitation.IsExpired(nowUtc),
                            row.InvitedBy,
                            MayInvite(caller.Role, row.Invitation.Role)))
        ];
    }

    /// <summary>
    /// Revokes an open invitation of the organisation, so its link stops
    /// working. Owners revoke any; Admins only invitations they could have
    /// sent themselves (Member invitations), by their role under the write lock.
    /// </summary>
    public async Task<InvitationFailure> RevokeAsync(
        OrganizationCaller caller,
        int invitationId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        caller = await organizationAccess.ReauthorizeAsync(caller, OrganizationRoleRequirement.OwnerOrAdmin, cancellationToken);

        var invitation =
            await dbContext.Invitations
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == invitationId &&
                        candidate.OrganizationId == caller.OrganizationId &&
                        candidate.AcceptedUtc == null &&
                        candidate.RevokedUtc == null,
                    cancellationToken);

        if (invitation is null)
        {
            return InvitationFailure.InvitationNotFound;
        }

        if (!MayInvite(caller.Role, invitation.Role))
        {
            return InvitationFailure.PermissionDenied;
        }

        invitation.Revoke(nowUtc);

        dbContext.AuditEvents.Add(
            AuditEvent.Record(
                AuditEventType.InvitationRevoked,
                caller.OrganizationId,
                caller.UserId,
                targetUserId: null,
                nowUtc,
                new { email = invitation.Email, role = invitation.Role.ToString(), reason = "revoked" }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InvitationFailure.None;
    }

    /// <summary>Owners may invite Admins and Members; Admins only Members.</summary>
    public static bool MayInvite(
        MembershipRole inviterRole,
        MembershipRole invitedRole)
    {
        return inviterRole switch
        {
            MembershipRole.Owner => invitedRole is MembershipRole.Admin or MembershipRole.Member,
            MembershipRole.Admin => invitedRole is MembershipRole.Member,
            _ => false
        };
    }

    private async Task<bool> InviterMayStillInviteAsync(
        Invitation invitation,
        CancellationToken cancellationToken)
    {
        var inviter =
            await dbContext.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == invitation.InvitedByUserId, cancellationToken);

        if (inviter.IsDisabled)
        {
            return false;
        }

        var inviterRole =
            await RoleOfAsync(invitation.OrganizationId, inviter.Id, cancellationToken);

        return inviterRole is not null &&
               MayInvite(inviterRole.Value, invitation.Role);
    }

    private async Task<MembershipRole?> RoleOfAsync(
        int organizationId,
        int userId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Memberships
            .AsNoTracking()
            .Where(
                membership =>
                    membership.OrganizationId == organizationId &&
                    membership.UserId == userId)
            .Select(membership => (MembershipRole?)membership.Role)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
