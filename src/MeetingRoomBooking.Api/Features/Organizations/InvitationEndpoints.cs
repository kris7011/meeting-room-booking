using System.Security.Claims;
using MeetingRoomBooking.Api.Features.Authentication;
using MeetingRoomBooking.Api.Features.Users;

namespace MeetingRoomBooking.Api.Features.Organizations;

public sealed record CreateInvitationRequest(
    string? Email,
    string? Role);

/// <summary>
/// The token is shown only in this response: the API stores nothing but its
/// hash. Send it to the invited person (for example in a link).
/// </summary>
public sealed record InvitationCreatedResponse(
    Guid OrganizationId,
    string OrganizationName,
    string Email,
    string Role,
    DateTimeOffset ExpiresUtc,
    string Token);

public sealed record AcceptInvitationRequest(
    string? Token);

/// <summary>An open invitation, without its token (which only the invitee has).</summary>
/// <param name="CanRevoke">The caller's role may revoke it (Admins only Member invitations).</param>
public sealed record InvitationResponse(
    int Id,
    string Email,
    string Role,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    bool IsExpired,
    string InvitedBy,
    bool CanRevoke);

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(
        this IEndpointRouteBuilder endpoints,
        EntraAuthenticationOptions options)
    {
        var create =
            options.IsConfigured
                ? endpoints
                    .MapPost("/api/organizations/{organizationId:guid}/invitations", CreateAsync)
                    .RequireAuthorization(EntraAuthentication.SignedInUserPolicy)
                : endpoints.MapPost(
                    "/api/organizations/{organizationId:guid}/invitations",
                    ApiProblems.AuthenticationNotConfigured);

        create
            .WithName("CreateInvitation")
            .WithTags("Organizations");

        var accept =
            options.IsConfigured
                ? endpoints
                    .MapPost("/api/invitations/accept", AcceptAsync)
                    .RequireAuthorization(EntraAuthentication.SignedInUserPolicy)
                : endpoints.MapPost("/api/invitations/accept", ApiProblems.AuthenticationNotConfigured);

        accept
            .WithName("AcceptInvitation")
            .WithTags("Organizations");

        return endpoints;
    }

    /// <summary>GET and DELETE …/invitations: Owners and Admins see open invitations and revoke them.</summary>
    public static RouteGroupBuilder MapOrganizationInvitationEndpoints(
        this RouteGroupBuilder organization)
    {
        organization.MapGet("/invitations", ListAsync)
            .RequireOwnerOrAdmin()
            .WithName("GetInvitations")
            .WithTags("Organizations");

        organization.MapDelete("/invitations/{invitationId:int}", RevokeAsync)
            .RequireOwnerOrAdmin()
            .WithName("RevokeInvitation")
            .WithTags("Organizations");

        return organization;
    }

    private static async Task<IResult> ListAsync(
        CurrentOrganization organization,
        InvitationService invitations,
        CancellationToken cancellationToken) =>
        Results.Ok(await invitations.ListOpenAsync(organization.Required(), cancellationToken));

    private static async Task<IResult> RevokeAsync(
        int invitationId,
        CurrentOrganization organization,
        InvitationService invitations,
        CancellationToken cancellationToken)
    {
        var failure =
            await invitations.RevokeAsync(organization.Required(), invitationId, cancellationToken);

        return failure == InvitationFailure.None
            ? Results.NoContent()
            : ToProblem(failure, errors: null);
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        CreateInvitationRequest request,
        ClaimsPrincipal principal,
        InvitationService invitations,
        CancellationToken cancellationToken)
    {
        var result =
            await invitations.CreateAsync(
                SignedInIdentity.From(principal),
                organizationId,
                request.Email,
                request.Role,
                cancellationToken);

        if (result.Value is not { } created)
        {
            return ToProblem(result.Failure, result.Errors);
        }

        return Results.Created(
            (string?)null,
            new InvitationCreatedResponse(
                created.OrganizationPublicId,
                created.OrganizationName,
                created.Email,
                created.Role.ToString(),
                created.ExpiresUtc,
                created.Token));
    }

    private static async Task<IResult> AcceptAsync(
        AcceptInvitationRequest request,
        ClaimsPrincipal principal,
        InvitationService invitations,
        CancellationToken cancellationToken)
    {
        var result =
            await invitations.AcceptAsync(
                SignedInIdentity.From(principal),
                request.Token,
                cancellationToken);

        if (result.Value is not { } accepted)
        {
            return ToProblem(result.Failure, result.Errors);
        }

        return Results.Ok(
            new OrganizationMembershipResponse(
                accepted.OrganizationPublicId,
                accepted.OrganizationName,
                accepted.IsLegacy,
                accepted.Role.ToString()));
    }

    private static IResult ToProblem(
        InvitationFailure failure,
        IDictionary<string, string[]>? errors)
    {
        return failure switch
        {
            InvitationFailure.InvalidRequest =>
                Results.ValidationProblem(errors ?? new Dictionary<string, string[]>()),

            InvitationFailure.NotRegistered =>
                ApiProblems.UserNotRegistered(),

            InvitationFailure.UserDisabled =>
                ApiProblems.UserDisabled(),

            InvitationFailure.OrganizationNotFound =>
                ApiProblems.Create(
                    StatusCodes.Status404NotFound,
                    "organization_not_found",
                    "Organisation not found",
                    "The organisation does not exist, or you are not a member of it."),

            InvitationFailure.PermissionDenied =>
                ApiProblems.Create(
                    StatusCodes.Status403Forbidden,
                    "organization_permission_denied",
                    "Not allowed",
                    "Only Owners and Admins may invite or revoke invitations, and only Owners may invite Admins or revoke their invitations."),

            InvitationFailure.InvitationNotFound =>
                ApiProblems.Create(
                    StatusCodes.Status404NotFound,
                    "invitation_not_found",
                    "Invitation not found",
                    "No open invitation matches this token or ID."),

            InvitationFailure.InvitationExpired =>
                ApiProblems.Create(
                    StatusCodes.Status410Gone,
                    "invitation_expired",
                    "Invitation expired",
                    "The invitation has expired. Ask for a new one."),

            InvitationFailure.InvitationNoLongerValid =>
                ApiProblems.Create(
                    StatusCodes.Status410Gone,
                    "invitation_no_longer_valid",
                    "Invitation no longer valid",
                    "The invitation has been revoked or used. Ask for a new one."),

            InvitationFailure.VerifiedEmailRequired =>
                ApiProblems.Create(
                    StatusCodes.Status403Forbidden,
                    "verified_email_required",
                    "Verified e-mail required",
                    "Your sign-in does not include an e-mail address Microsoft has verified, so the invitation cannot be matched to you."),

            InvitationFailure.EmailMismatch =>
                ApiProblems.Create(
                    StatusCodes.Status403Forbidden,
                    "invitation_email_mismatch",
                    "Invitation is for someone else",
                    "The invitation was sent to another e-mail address than the one you signed in with."),

            InvitationFailure.AlreadyMember =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "already_member",
                    "Already a member",
                    "You are already a member of this organisation."),

            _ =>
                throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown invitation failure.")
        };
    }
}
