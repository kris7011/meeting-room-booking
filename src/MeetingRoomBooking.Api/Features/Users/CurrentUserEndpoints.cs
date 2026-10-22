using System.Security.Claims;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MeetingRoomBooking.Api.Features.Users;

/// <summary>The caller as the API sees them, from the validated access token.</summary>
public sealed record CurrentUserResponse(
    string DisplayName,
    string? Username,
    string TenantId,
    RegisteredUserResponse? User);

/// <summary>The API's own user record for the identity, if one exists.</summary>
public sealed record RegisteredUserResponse(
    Guid PublicId,
    string DisplayName);

public static class CurrentUserEndpoints
{
    public static IEndpointRouteBuilder MapCurrentUserEndpoints(
        this IEndpointRouteBuilder endpoints,
        EntraAuthenticationOptions options)
    {
        var endpoint =
            options.IsConfigured
                ? endpoints
                    .MapGet("/api/me", GetAsync)
                    .RequireAuthorization(EntraAuthentication.SignedInUserPolicy)
                : endpoints.MapGet("/api/me", NotConfigured);

        endpoint
            .WithName("GetCurrentUser")
            .WithTags("Users");

        return endpoints;
    }

    /// <summary>
    /// Read-only: looks the identity up by the validated issuer and object ID
    /// (the key of <c>UserIdentities</c>) and creates nothing. Linking
    /// identities to users belongs with invitations and memberships.
    /// </summary>
    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        BookingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var issuer =
            Required(principal, EntraAuthentication.Claims.Issuer);

        var objectId =
            Required(principal, EntraAuthentication.Claims.ObjectId);

        var tenantId =
            Required(principal, EntraAuthentication.Claims.TenantId);

        var username =
            principal.FindFirstValue(EntraAuthentication.Claims.PreferredUsername);

        var displayName =
            principal.FindFirstValue(EntraAuthentication.Claims.Name)
            ?? username
            ?? objectId;

        var user =
            await dbContext.UserIdentities
                .AsNoTracking()
                .Where(
                    identity =>
                        identity.Provider == IdentityProvider.Entra &&
                        identity.Issuer == issuer &&
                        identity.Subject == objectId)
                .Join(
                    dbContext.Users,
                    identity => identity.UserId,
                    candidate => candidate.Id,
                    (_, candidate) => candidate)
                .Select(
                    candidate =>
                        new
                        {
                            candidate.PublicId,
                            candidate.DisplayName,
                            candidate.DisabledUtc
                        })
                .SingleOrDefaultAsync(cancellationToken);

        if (user?.DisabledUtc is not null)
        {
            var problem =
                new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "User disabled",
                    Detail = "This user account has been disabled."
                };

            problem.Extensions["code"] = "user_disabled";

            return Results.Problem(problem);
        }

        return Results.Ok(
            new CurrentUserResponse(
                displayName,
                username,
                tenantId,
                user is null
                    ? null
                    : new RegisteredUserResponse(
                        user.PublicId,
                        user.DisplayName)));
    }

    private static IResult NotConfigured()
    {
        var problem =
            new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Sign-in is not configured",
                Detail = "The API has no Entra settings, so it cannot validate access tokens."
            };

        problem.Extensions["code"] = "authentication_not_configured";

        return Results.Problem(problem);
    }

    // The policy guarantees tid and oid; iss is always present on a
    // validated token.
    private static string Required(
        ClaimsPrincipal principal,
        string claimType)
    {
        return principal.FindFirstValue(claimType)
            ?? throw new InvalidOperationException(
                $"The validated token has no {claimType} claim.");
    }
}
