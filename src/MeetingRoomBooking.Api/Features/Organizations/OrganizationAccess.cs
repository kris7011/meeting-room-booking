using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Authentication;
using MeetingRoomBooking.Api.Features.Users;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Organizations;

public enum OrganizationAccessFailure
{
    None,
    NotRegistered,
    UserDisabled,

    /// <summary>No such organisation, or the caller is not a member of it.</summary>
    OrganizationNotFound,

    /// <summary>The caller is a member, but their role may not do this.</summary>
    PermissionDenied
}

/// <summary>
/// The signed-in caller and their membership of the organisation a request
/// works in. Everything here comes from the database, looked up by the
/// validated token's identity: never from the request body.
/// </summary>
public sealed record OrganizationCaller(
    int UserId,
    Guid UserPublicId,
    string DisplayName,
    int OrganizationId,
    Guid OrganizationPublicId,
    string OrganizationName,
    bool IsLegacy,
    MembershipRole Role)
{
    public bool IsOwner =>
        Role == MembershipRole.Owner;

    /// <summary>Owners and Admins manage rooms and invite Members.</summary>
    public bool IsOwnerOrAdmin =>
        Role is MembershipRole.Owner or MembershipRole.Admin;
}

public sealed record OrganizationAccessResult(
    OrganizationAccessFailure Failure,
    OrganizationCaller? Caller);

public sealed record RegisteredUserResult(
    OrganizationAccessFailure Failure,
    User? User);

/// <summary>
/// The caller lost the access a change needs while their request was running
/// (see <see cref="OrganizationAccessService.ReauthorizeAsync"/>).
/// <see cref="OrganizationAccessFilter"/> answers it with the problem it would
/// have returned had the change happened before the request arrived.
/// </summary>
public sealed class OrganizationAccessDeniedException(
    OrganizationAccessFailure failure)
    : Exception($"The caller's organisation access changed during the request: {failure}.")
{
    public OrganizationAccessFailure Failure { get; } = failure;
}

/// <summary>
/// Decides who may work in an organisation: a registered, enabled user with
/// a membership of it. An organisation ID alone never grants anything.
/// </summary>
public sealed class OrganizationAccessService(
    BookingDbContext dbContext)
{
    /// <summary>The caller's user, if they are registered and enabled.</summary>
    public async Task<RegisteredUserResult> ResolveUserAsync(
        SignedInIdentity identity,
        CancellationToken cancellationToken)
    {
        var user =
            await dbContext
                .RegisteredUser(identity)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return new RegisteredUserResult(OrganizationAccessFailure.NotRegistered, null);
        }

        return user.IsDisabled
            ? new RegisteredUserResult(OrganizationAccessFailure.UserDisabled, null)
            : new RegisteredUserResult(OrganizationAccessFailure.None, user);
    }

    public async Task<OrganizationAccessResult> ResolveAsync(
        SignedInIdentity identity,
        Guid organizationPublicId,
        CancellationToken cancellationToken)
    {
        var resolved =
            await ResolveUserAsync(identity, cancellationToken);

        if (resolved.User is not { } user)
        {
            return new OrganizationAccessResult(resolved.Failure, null);
        }

        var membership =
            await dbContext.Memberships
                .AsNoTracking()
                .Where(candidate => candidate.UserId == user.Id)
                .Join(
                    dbContext.Organizations.Where(
                        organization =>
                            organization.PublicId == organizationPublicId &&
                            organization.DeletedUtc == null),
                    candidate => candidate.OrganizationId,
                    organization => organization.Id,
                    (candidate, organization) =>
                        new
                        {
                            organization.Id,
                            organization.PublicId,
                            organization.Name,
                            organization.IsLegacy,
                            candidate.Role
                        })
                .SingleOrDefaultAsync(cancellationToken);

        if (membership is null)
        {
            return new OrganizationAccessResult(OrganizationAccessFailure.OrganizationNotFound, null);
        }

        return new OrganizationAccessResult(
            OrganizationAccessFailure.None,
            new OrganizationCaller(
                user.Id,
                user.PublicId,
                user.DisplayName,
                membership.Id,
                membership.PublicId,
                membership.Name,
                membership.IsLegacy,
                membership.Role));
    }

    /// <summary>
    /// Checks the caller's access again, as it is now. A service calls it
    /// inside the transaction that makes its change, after
    /// <c>BEGIN IMMEDIATE</c> has taken the write lock and before it writes
    /// anything: the <paramref name="caller"/> from the access filter is a
    /// snapshot from the start of the request, and a concurrent request may
    /// have disabled, removed or demoted them since. Under the write lock no
    /// other request can change that again until the transaction ends.
    /// </summary>
    /// <returns>The caller with the role they have now.</returns>
    /// <exception cref="OrganizationAccessDeniedException">
    /// The caller is no longer registered and enabled, no longer a member, or
    /// their role no longer meets <paramref name="requirement"/>.
    /// </exception>
    public async Task<OrganizationCaller> ReauthorizeAsync(
        OrganizationCaller caller,
        OrganizationRoleRequirement requirement,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Reauthorize inside the transaction that makes the change; outside it, the answer can be stale again before the change is written.");
        }

        var user =
            await dbContext.Users
                .AsNoTracking()
                .Where(candidate => candidate.Id == caller.UserId)
                .Select(candidate => new { candidate.DisabledUtc })
                .SingleOrDefaultAsync(cancellationToken);

        var role =
            await dbContext.Memberships
                .AsNoTracking()
                .Where(
                    membership =>
                        membership.UserId == caller.UserId &&
                        membership.OrganizationId == caller.OrganizationId &&
                        dbContext.Organizations.Any(
                            organization =>
                                organization.Id == membership.OrganizationId &&
                                organization.DeletedUtc == null))
                .Select(membership => (MembershipRole?)membership.Role)
                .SingleOrDefaultAsync(cancellationToken);

        var failure =
            user is null ? OrganizationAccessFailure.NotRegistered
            : user.DisabledUtc is not null ? OrganizationAccessFailure.UserDisabled
            : role is not { } current ? OrganizationAccessFailure.OrganizationNotFound
            : !requirement.Admits(current) ? OrganizationAccessFailure.PermissionDenied
            : OrganizationAccessFailure.None;

        return failure == OrganizationAccessFailure.None
            ? caller with { Role = role!.Value }
            : throw new OrganizationAccessDeniedException(failure);
    }

    public static IResult ToProblem(
        OrganizationAccessFailure failure) =>
        failure switch
        {
            OrganizationAccessFailure.NotRegistered => ApiProblems.UserNotRegistered(),
            OrganizationAccessFailure.UserDisabled => ApiProblems.UserDisabled(),
            OrganizationAccessFailure.OrganizationNotFound => ApiProblems.OrganizationNotFound(),
            OrganizationAccessFailure.PermissionDenied => ApiProblems.OrganizationPermissionDenied(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Not a failure.")
        };
}

/// <summary>
/// The organisation the current request works in. Set by
/// <see cref="OrganizationAccessFilter"/> before the endpoint runs; an
/// endpoint behind the filter reads <see cref="Caller"/>.
/// </summary>
public sealed class CurrentOrganization
{
    private OrganizationCaller? caller;

    /// <summary>
    /// The member making the request. Null only on the legacy endpoints in
    /// anonymous demo mode (<c>LegacyApi:AllowAnonymous</c>).
    /// </summary>
    public OrganizationCaller? Caller => caller;

    internal void Set(
        OrganizationCaller value)
    {
        if (caller is not null)
        {
            throw new InvalidOperationException("The organisation of a request is set once.");
        }

        caller = value;
    }
}

/// <summary>Endpoint metadata: only these roles may call the endpoint.</summary>
public sealed class OrganizationRoleRequirement(
    params MembershipRole[] roles)
{
    public static readonly OrganizationRoleRequirement AnyMember =
        new(MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Member);

    public static readonly OrganizationRoleRequirement OwnerOrAdmin =
        new(MembershipRole.Owner, MembershipRole.Admin);

    public static readonly OrganizationRoleRequirement OwnerOnly =
        new(MembershipRole.Owner);

    public IReadOnlyCollection<MembershipRole> Roles { get; } = roles;

    public bool Admits(
        MembershipRole role) =>
        Roles.Contains(role);
}

/// <summary>
/// Runs before every endpoint of an organisation: resolves the caller's
/// membership of the organisation in the route (or of a fixed one, for the
/// legacy endpoints) and checks the endpoint's role requirement. Unregistered
/// and disabled users get 403, non-members 404, members without the role 403.
/// A service that finds the access gone when it checks again under the write
/// lock (<see cref="OrganizationAccessDeniedException"/>) gets the same answers.
/// </summary>
public sealed class OrganizationAccessFilter(
    Guid? fixedOrganizationPublicId = null)
    : IEndpointFilter
{
    public const string RouteParameter = "organizationId";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        Guid organizationPublicId;

        if (fixedOrganizationPublicId is { } fixedId)
        {
            organizationPublicId = fixedId;
        }
        else if (!Guid.TryParse(
                     httpContext.Request.RouteValues[RouteParameter] as string,
                     out organizationPublicId))
        {
            return ApiProblems.OrganizationNotFound();
        }

        var access =
            httpContext.RequestServices.GetRequiredService<OrganizationAccessService>();

        var result =
            await access.ResolveAsync(
                SignedInIdentity.From(httpContext.User),
                organizationPublicId,
                httpContext.RequestAborted);

        if (result.Caller is not { } caller)
        {
            return OrganizationAccessService.ToProblem(result.Failure);
        }

        var requirement =
            httpContext.GetEndpoint()?.Metadata.GetMetadata<OrganizationRoleRequirement>();

        if (requirement is not null &&
            !requirement.Admits(caller.Role))
        {
            return ApiProblems.OrganizationPermissionDenied();
        }

        httpContext.RequestServices.GetRequiredService<CurrentOrganization>().Set(caller);

        try
        {
            return await next(context);
        }
        catch (OrganizationAccessDeniedException denied)
        {
            // The service's transaction was rolled back: nothing was written.
            return OrganizationAccessService.ToProblem(denied.Failure);
        }
    }
}

public static class OrganizationAccessExtensions
{
    public const string OrganizationRoutePrefix =
        "/api/organizations/{" + OrganizationAccessFilter.RouteParameter + ":guid}";

    /// <summary>
    /// The group every organisation-specific endpoint is mapped in: a valid
    /// access token, a registered and enabled user, and a membership of the
    /// organisation in the route are required before any endpoint runs.
    /// </summary>
    public static RouteGroupBuilder MapOrganizationGroup(
        this IEndpointRouteBuilder endpoints,
        EntraAuthenticationOptions options)
    {
        var group =
            endpoints.MapGroup(OrganizationRoutePrefix);

        return options.IsConfigured
            ? group
                .RequireAuthorization(EntraAuthentication.SignedInUserPolicy)
                .AddEndpointFilter(new OrganizationAccessFilter())
            : group.AddEndpointFilter(
                (_, _) => ValueTask.FromResult<object?>(ApiProblems.AuthenticationNotConfigured()));
    }

    /// <summary>Only members with one of these roles may call the endpoint (403 otherwise).</summary>
    public static TBuilder RequireOrganizationRole<TBuilder>(
        this TBuilder builder,
        params MembershipRole[] roles)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new OrganizationRoleRequirement(roles));

    /// <summary>Owners and Admins only.</summary>
    public static TBuilder RequireOwnerOrAdmin<TBuilder>(
        this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(OrganizationRoleRequirement.OwnerOrAdmin);

    /// <summary>The caller set by the filter; throws if an endpoint is mapped outside an organisation group.</summary>
    public static OrganizationCaller Required(
        this CurrentOrganization organization) =>
        organization.Caller
        ?? throw new InvalidOperationException(
            "This endpoint must be mapped inside an organisation group.");
}
