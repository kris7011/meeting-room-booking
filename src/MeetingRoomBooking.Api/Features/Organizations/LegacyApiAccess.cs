using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Authentication;

namespace MeetingRoomBooking.Api.Features.Organizations;

/// <summary>
/// How the original <c>/api/rooms</c> and <c>/api/bookings</c> endpoints are
/// protected (the <c>LegacyApi</c> configuration section). They only ever
/// work on the legacy organisation.
/// </summary>
public sealed class LegacyApiOptions
{
    public const string SectionName = "LegacyApi";

    /// <summary>
    /// False (the default): the legacy endpoints require a signed-in,
    /// registered member of the legacy organisation, exactly like
    /// <c>/api/organizations/{legacy}/…</c>, and the member's role decides
    /// what they may change.
    /// <para>
    /// True: anonymous demo mode, as before sign-in existed. Allowed only in
    /// the Development and Testing environments; the API refuses to start
    /// with it anywhere else, so real organisation data is never anonymous
    /// in production.
    /// </para>
    /// </summary>
    public bool AllowAnonymous { get; set; }

    public static LegacyApiOptions Read(
        WebApplicationBuilder builder)
    {
        var options =
            builder.Configuration
                .GetSection(SectionName)
                .Get<LegacyApiOptions>()
            ?? new LegacyApiOptions();

        if (options.AllowAnonymous &&
            !builder.Environment.IsDevelopment() &&
            !builder.Environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                $"{SectionName}:AllowAnonymous is only allowed in the Development and Testing environments, " +
                $"not in {builder.Environment.EnvironmentName}. Anonymous callers must never reach organisation data in production.");
        }

        return options;
    }
}

public static class LegacyApiAccess
{
    /// <summary>
    /// A group for the original endpoints. In demo mode it is anonymous and
    /// a token is ignored; otherwise every request needs a member of the
    /// legacy organisation (401 without a valid token, 403 unregistered or
    /// disabled, 404 not a member).
    /// </summary>
    public static RouteGroupBuilder MapLegacyGroup(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        LegacyApiOptions legacyOptions,
        EntraAuthenticationOptions entraOptions)
    {
        var group =
            endpoints.MapGroup(prefix);

        if (legacyOptions.AllowAnonymous)
        {
            return group;
        }

        return entraOptions.IsConfigured
            ? group
                .RequireAuthorization(EntraAuthentication.SignedInUserPolicy)
                .AddEndpointFilter(new OrganizationAccessFilter(LegacyOrganization.PublicId))
            : group.AddEndpointFilter(
                (_, _) => ValueTask.FromResult<object?>(ApiProblems.AuthenticationNotConfigured()));
    }
}
