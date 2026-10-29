using System.Security.Claims;
using MeetingRoomBooking.Api.Features.Authentication;

namespace MeetingRoomBooking.Api.Features.Users;

/// <summary>The code is shown only in this response; the API stores its hash.</summary>
public sealed record StartIdentityLinkResponse(
    Guid LinkId,
    string Code,
    DateTimeOffset ExpiresUtc);

public sealed record ClaimIdentityLinkRequest(
    string? Code);

/// <summary>
/// What the person must confirm: which account the new sign-in will join.
/// Only the session that started the request has the code, so this tells
/// the claimant nothing they do not already know.
/// </summary>
public sealed record ClaimIdentityLinkResponse(
    Guid LinkId,
    string AccountDisplayName,
    string Provider,
    DateTimeOffset ExpiresUtc);

/// <summary>One way the user signs in. Issuer and object ID stay on the server.</summary>
public sealed record LinkedIdentityResponse(
    string Provider,
    bool IsPersonalMicrosoftAccount,
    string? VerifiedEmail,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastUsedUtc,
    bool IsCurrent);

public static class IdentityLinkEndpoints
{
    public static IEndpointRouteBuilder MapIdentityLinkEndpoints(
        this IEndpointRouteBuilder endpoints,
        EntraAuthenticationOptions options)
    {
        Map(endpoints.MapGet("/api/me/identities", ListAsync), "GetIdentities", options);
        Map(endpoints.MapPost("/api/me/identity-links", StartAsync), "StartIdentityLink", options);
        Map(endpoints.MapPost("/api/identity-links/claim", ClaimAsync), "ClaimIdentityLink", options);
        Map(endpoints.MapPost("/api/me/identity-links/{linkId:guid}/confirm", ConfirmAsync), "ConfirmIdentityLink", options);
        Map(endpoints.MapDelete("/api/me/identity-links/{linkId:guid}", CancelAsync), "CancelIdentityLink", options);

        return endpoints;
    }

    private static void Map(
        RouteHandlerBuilder endpoint,
        string name,
        EntraAuthenticationOptions options)
    {
        endpoint
            .WithName(name)
            .WithTags("Users");

        if (options.IsConfigured)
        {
            endpoint.RequireAuthorization(EntraAuthentication.SignedInUserPolicy);
        }
        else
        {
            endpoint.AddEndpointFilter((_, _) => ValueTask.FromResult<object?>(ApiProblems.AuthenticationNotConfigured()));
        }
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        IdentityLinkService links,
        CancellationToken cancellationToken)
    {
        var result =
            await links.ListIdentitiesAsync(SignedInIdentity.From(principal), cancellationToken);

        return result.Value is { } identities
            ? Results.Ok(identities.Select(ToResponse))
            : ToProblem(result.Failure);
    }

    private static async Task<IResult> StartAsync(
        ClaimsPrincipal principal,
        IdentityLinkService links,
        CancellationToken cancellationToken)
    {
        var result =
            await links.StartAsync(SignedInIdentity.From(principal), cancellationToken);

        return result.Value is { } started
            ? Results.Created(
                (string?)null,
                new StartIdentityLinkResponse(started.LinkId, started.Code, started.ExpiresUtc))
            : ToProblem(result.Failure);
    }

    private static async Task<IResult> ClaimAsync(
        ClaimIdentityLinkRequest request,
        ClaimsPrincipal principal,
        IdentityLinkService links,
        CancellationToken cancellationToken)
    {
        var result =
            await links.ClaimAsync(SignedInIdentity.From(principal), request.Code, cancellationToken);

        return result.Value is { } claimed
            ? Results.Ok(
                new ClaimIdentityLinkResponse(
                    claimed.LinkId,
                    claimed.AccountDisplayName,
                    claimed.Provider.ToString(),
                    claimed.ExpiresUtc))
            : ToProblem(result.Failure);
    }

    private static async Task<IResult> ConfirmAsync(
        Guid linkId,
        ClaimsPrincipal principal,
        IdentityLinkService links,
        CancellationToken cancellationToken)
    {
        var result =
            await links.ConfirmAsync(SignedInIdentity.From(principal), linkId, cancellationToken);

        return result.Value is { } identities
            ? Results.Ok(identities.Select(ToResponse))
            : ToProblem(result.Failure);
    }

    private static async Task<IResult> CancelAsync(
        Guid linkId,
        ClaimsPrincipal principal,
        IdentityLinkService links,
        CancellationToken cancellationToken)
    {
        var failure =
            await links.CancelAsync(SignedInIdentity.From(principal), linkId, cancellationToken);

        return failure == IdentityLinkFailure.None
            ? Results.NoContent()
            : ToProblem(failure);
    }

    private static LinkedIdentityResponse ToResponse(
        LinkedIdentity identity) =>
        new(
            identity.Provider.ToString(),
            identity.IsPersonalMicrosoftAccount,
            identity.VerifiedEmail,
            identity.CreatedUtc,
            identity.LastUsedUtc,
            identity.IsCurrent);

    private static IResult ToProblem(
        IdentityLinkFailure failure)
    {
        return failure switch
        {
            IdentityLinkFailure.InvalidRequest =>
                Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["code"] = ["The link code is missing or malformed."]
                    }),

            IdentityLinkFailure.NotRegistered =>
                ApiProblems.UserNotRegistered(),

            IdentityLinkFailure.UserDisabled =>
                ApiProblems.UserDisabled(),

            IdentityLinkFailure.LinkNotFound =>
                ApiProblems.Create(
                    StatusCodes.Status404NotFound,
                    "identity_link_not_found",
                    "Link request not found",
                    "You have no link request with this ID."),

            IdentityLinkFailure.LinkInvalid =>
                ApiProblems.Create(
                    StatusCodes.Status400BadRequest,
                    "identity_link_invalid",
                    "Link request no longer valid",
                    "The code is unknown, or the request has expired, been used, cancelled or claimed by another sign-in. Start again."),

            IdentityLinkFailure.NotClaimed =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "identity_link_not_claimed",
                    "Not signed in with the second identity yet",
                    "Sign in with the identity to add and claim the code before confirming."),

            IdentityLinkFailure.AlreadyLinked =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "identity_already_linked",
                    "Already linked",
                    "This sign-in already belongs to your account."),

            IdentityLinkFailure.BelongsToAnotherUser =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "identity_belongs_to_another_user",
                    "Sign-in belongs to another account",
                    "This sign-in is a Room Booking account of its own. Accounts are never merged, so it cannot be added."),

            _ =>
                throw new ArgumentOutOfRangeException(nameof(failure), failure, null)
        };
    }
}
