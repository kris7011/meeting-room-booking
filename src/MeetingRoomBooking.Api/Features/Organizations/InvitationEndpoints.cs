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
                    "Only Owners and Admins may invite, and only Owners may invite Admins."),

            InvitationFailure.InvitationNotFound =>
                ApiProblems.Create(
                    StatusCodes.Status404NotFound,
                    "invitation_not_found",
                    "Invitation not found",
                    "No invitation matches this token."),

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
