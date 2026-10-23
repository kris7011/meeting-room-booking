using System.Security.Claims;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Authentication;
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
    string DisplayName,
    IReadOnlyList<OrganizationMembershipResponse> Organizations);

/// <summary>An organisation the user is a member of, and their role there.</summary>
public sealed record OrganizationMembershipResponse(
    Guid PublicId,
    string Name,
    bool IsLegacy,
    string Role);

/// <summary>The result of POST /api/me/registration.</summary>
/// <param name="Created">True only for the request that created the user.</param>
public sealed record RegistrationResponse(
    bool Created,
    CurrentUserResponse CurrentUser);

public static class CurrentUserEndpoints
{
    public static IEndpointRouteBuilder MapCurrentUserEndpoints(
        this IEndpointRouteBuilder endpoints,
        EntraAuthenticationOptions options)
    {
        if (!options.IsConfigured)
        {
            endpoints.MapGet("/api/me", ApiProblems.AuthenticationNotConfigured)
                .WithName("GetCurrentUser")
                .WithTags("Users");

            endpoints.MapPost("/api/me/registration", ApiProblems.AuthenticationNotConfigured)
                .WithName("RegisterCurrentUser")
                .WithTags("Users");

            return endpoints;
        }

        endpoints
            .MapGet("/api/me", GetAsync)
            .RequireAuthorization(EntraAuthentication.SignedInUserPolicy)
            .WithName("GetCurrentUser")
            .WithTags("Users");

        endpoints
            .MapPost("/api/me/registration", RegisterAsync)
            .RequireAuthorization(EntraAuthentication.SignedInUserPolicy)
            .WithName("RegisterCurrentUser")
            .WithTags("Users");

        return endpoints;
    }

    /// <summary>
    /// Read-only: looks the identity up by the validated issuer and object ID
    /// (the key of <c>UserIdentities</c>) and creates or changes nothing.
    /// <c>user</c> is null until the identity has been registered.
    /// </summary>
    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        BookingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var identity =
            SignedInIdentity.From(principal);

        var user =
            await dbContext
                .RegisteredUser(identity)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);

        if (user?.IsDisabled == true)
        {
            return ApiProblems.UserDisabled();
        }

        return Results.Ok(
            await ToResponseAsync(identity, user, dbContext, cancellationToken));
    }

    /// <summary>
    /// Registers the signed-in identity: 201 when this request created the
    /// user, 200 when it was already registered (same body, nothing created).
    /// The identity comes from the token alone; the request has no body.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        ClaimsPrincipal principal,
        UserRegistrationService registration,
        BookingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var identity =
            SignedInIdentity.From(principal);

        var result =
            await registration.RegisterAsync(identity, cancellationToken);

        if (result.Outcome == RegistrationOutcome.Disabled)
        {
            return ApiProblems.UserDisabled();
        }

        var response =
            new RegistrationResponse(
                result.Outcome == RegistrationOutcome.Created,
                await ToResponseAsync(identity, result.User, dbContext, cancellationToken));

        return result.Outcome == RegistrationOutcome.Created
            ? Results.Created("/api/me", response)
            : Results.Ok(response);
    }

    private static async Task<CurrentUserResponse> ToResponseAsync(
        SignedInIdentity identity,
        User? user,
        BookingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        RegisteredUserResponse? registered = null;

        if (user is not null)
        {
            var memberships =
                await dbContext.Memberships
                    .AsNoTracking()
                    .Where(membership => membership.UserId == user.Id)
                    .Join(
                        dbContext.Organizations.Where(
                            organization => organization.DeletedUtc == null),
                        membership => membership.OrganizationId,
                        organization => organization.Id,
                        (membership, organization) =>
                            new
                            {
                                organization.PublicId,
                                organization.Name,
                                organization.IsLegacy,
                                membership.Role
                            })
                    .OrderBy(organization => organization.Name)
                    .ToArrayAsync(cancellationToken);

            registered =
                new RegisteredUserResponse(
                    user.PublicId,
                    user.DisplayName,
                    [
                        .. memberships.Select(
                            membership =>
                                new OrganizationMembershipResponse(
                                    membership.PublicId,
                                    membership.Name,
                                    membership.IsLegacy,
                                    membership.Role.ToString()))
                    ]);
        }

        return new CurrentUserResponse(
            identity.DisplayName,
            identity.Username,
            identity.TenantId,
            registered);
    }
}
