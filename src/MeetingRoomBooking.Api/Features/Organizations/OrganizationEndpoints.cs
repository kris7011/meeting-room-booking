using System.Security.Claims;
using MeetingRoomBooking.Api.Features.Authentication;
using MeetingRoomBooking.Api.Features.Users;
using Microsoft.AspNetCore.Mvc;

namespace MeetingRoomBooking.Api.Features.Organizations;

public static class OrganizationEndpoints
{
    /// <summary>
    /// GET and POST /api/organizations: the caller's organisations, and
    /// creating a new one. A registered, enabled user is required; no
    /// membership, since the point is to get one.
    /// </summary>
    public static IEndpointRouteBuilder MapOrganizationEndpoints(
        this IEndpointRouteBuilder endpoints,
        EntraAuthenticationOptions options)
    {
        var group =
            endpoints.MapGroup("/api/organizations")
                .WithTags("Organisations");

        if (options.IsConfigured)
        {
            group.RequireAuthorization(EntraAuthentication.SignedInUserPolicy);
        }
        else
        {
            group.AddEndpointFilter(
                (_, _) => ValueTask.FromResult<object?>(ApiProblems.AuthenticationNotConfigured()));
        }

        group.MapGet("", ListAsync)
            .WithName("GetOrganizations");

        group.MapPost("", CreateAsync)
            .WithName("CreateOrganization");

        return endpoints;
    }

    /// <summary>GET and PUT /api/organizations/{organizationId}: details for members, renaming for Owners.</summary>
    public static RouteGroupBuilder MapOrganizationDetailEndpoints(
        this RouteGroupBuilder organization)
    {
        organization.MapGet("", GetAsync)
            .WithName("GetOrganization")
            .WithTags("Organisations");

        organization.MapPut("", RenameAsync)
            .RequireOrganizationRole(MembershipRole.Owner)
            .WithName("RenameOrganization")
            .WithTags("Organisations");

        return organization;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        OrganizationAccessService access,
        OrganizationService organizations,
        CancellationToken cancellationToken)
    {
        var resolved =
            await access.ResolveUserAsync(SignedInIdentity.From(principal), cancellationToken);

        if (resolved.User is not { } user)
        {
            return OrganizationAccessService.ToProblem(resolved.Failure);
        }

        return Results.Ok(await organizations.ListAsync(user.Id, cancellationToken));
    }

    private static async Task<IResult> CreateAsync(
        CreateOrganizationRequest request,
        [FromHeader(Name = OrganizationService.IdempotencyKeyHeader)] string? idempotencyKey,
        ClaimsPrincipal principal,
        OrganizationService organizations,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result =
            await organizations.CreateAsync(
                SignedInIdentity.From(principal),
                idempotencyKey,
                request.Name,
                cancellationToken);

        if (result.Organization is not { } organization)
        {
            return ToProblem(result);
        }

        if (result.Created)
        {
            return Results.Created($"/api/organizations/{organization.PublicId}", organization);
        }

        // A repeated request: the organisation the first one created.
        response.Headers["Idempotent-Replayed"] = "true";
        return Results.Ok(organization);
    }

    private static async Task<IResult> GetAsync(
        CurrentOrganization organization,
        OrganizationService organizations,
        CancellationToken cancellationToken) =>
        Results.Ok(await organizations.GetDetailsAsync(organization.Required(), cancellationToken));

    private static async Task<IResult> RenameAsync(
        RenameOrganizationRequest request,
        CurrentOrganization organization,
        OrganizationService organizations,
        CancellationToken cancellationToken)
    {
        var result =
            await organizations.RenameAsync(organization.Required(), request.Name, cancellationToken);

        return result.Organization is { } renamed
            ? Results.Ok(renamed)
            : ToProblem(result);
    }

    private static IResult ToProblem(
        OrganizationResult result) =>
        result.Failure switch
        {
            OrganizationFailure.InvalidRequest =>
                Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>()),

            OrganizationFailure.NotRegistered =>
                ApiProblems.UserNotRegistered(),

            OrganizationFailure.UserDisabled =>
                ApiProblems.UserDisabled(),

            OrganizationFailure.OrganizationNotFound =>
                ApiProblems.OrganizationNotFound(),

            OrganizationFailure.IdempotencyKeyReused =>
                ApiProblems.Create(
                    StatusCodes.Status422UnprocessableEntity,
                    "idempotency_key_reused",
                    "Idempotency-Key already used",
                    "This Idempotency-Key was already used to create an organisation with another name. Use a new key for a new organisation."),

            OrganizationFailure.CreationLimitReached =>
                ApiProblems.Create(
                    StatusCodes.Status429TooManyRequests,
                    "organization_creation_limit",
                    "Too many new organisations",
                    $"You can create at most {OrganizationService.MaximumCreatedPerDay} organisations in 24 hours."),

            OrganizationFailure.LegacyOrganizationReadOnly =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "legacy_organization_read_only",
                    "The legacy organisation cannot be renamed",
                    "The legacy organisation's name is fixed."),

            _ =>
                throw new ArgumentOutOfRangeException(nameof(result), result.Failure, "Unknown organisation failure.")
        };
}
