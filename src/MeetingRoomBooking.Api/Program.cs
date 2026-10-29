using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features;
using MeetingRoomBooking.Api.Features.Authentication;
using MeetingRoomBooking.Api.Features.Bookings;
using MeetingRoomBooking.Api.Features.Organizations;
using MeetingRoomBooking.Api.Features.Rooms;
using MeetingRoomBooking.Api.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder =
    WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString(
        "BookingDatabase")
    ?? throw new InvalidOperationException(
        "The BookingDatabase connection string is missing.");

builder.Services.AddProblemDetails();

// Browser clients served from another origin (the Expo web dev server on
// http://localhost:8081) need CORS. Only Development allows it, and only for
// the origins listed in appsettings.Development.json: other environments
// send no CORS headers, so browsers keep blocking cross-origin calls there.
// Idempotency-Key is allowed so the web app can repeat a booking safely.
const string DevelopmentClientsCorsPolicy =
    "DevelopmentClients";

var corsAllowedOrigins =
    builder.Environment.IsDevelopment()
        ? builder.Configuration
              .GetSection("Cors:AllowedOrigins")
              .Get<string[]>() ?? []
        : [];

if (corsAllowedOrigins.Length > 0)
{
    builder.Services.AddCors(
        options =>
            options.AddPolicy(
                DevelopmentClientsCorsPolicy,
                policy =>
                    policy
                        .WithOrigins(corsAllowedOrigins)
                        .WithMethods("GET", "POST", "PUT", "DELETE")
                        .WithHeaders("Accept", "Content-Type", IdempotencyHeaders.Key)));
}

builder.Services.AddDbContext<BookingDbContext>(
    options =>
        options.UseSqlite(connectionString));

// The API's notion of "now" (e.g. to reject bookings that start in the
// past). Tests replace it with a clock they control.
builder.Services.TryAddSingleton(TimeProvider.System);

builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<UserRegistrationService>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<OrganizationAccessService>();
builder.Services.AddScoped<CurrentOrganization>();
builder.Services.AddScoped<OrganizationService>();
builder.Services.AddScoped<RoomAdministrationService>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<IdentityLinkService>();

// Microsoft Entra ID access tokens (Entra section in configuration). Every
// organisation endpoint requires one, plus a membership of the organisation.
// Without Entra:ApiClientId, the signed-in endpoints answer 503.
var entraOptions =
    builder.AddEntraAuthentication();

// The original /api/rooms and /api/bookings endpoints: members of the legacy
// organisation only, unless LegacyApi:AllowAnonymous turns on the anonymous
// demo mode (Development and Testing only; the API refuses to start with it
// anywhere else).
var legacyApiOptions =
    LegacyApiOptions.Read(builder);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app =
    builder.Build();

app.UseExceptionHandler();

if (corsAllowedOrigins.Length > 0)
{
    app.UseCors(DevelopmentClientsCorsPolicy);
}

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment() &&
    !app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.MapGet(
        "/health",
        (TimeProvider timeProvider) =>
            Results.Ok(
                new
                {
                    status = "Healthy",
                    utcTimestamp =
                        timeProvider.GetUtcNow()
                }))
    .WithName("GetHealth")
    .WithTags("System");

app.MapLegacyGroup("/api/rooms", legacyApiOptions, entraOptions)
    .MapRoomEndpoints();

app.MapLegacyGroup("/api/bookings", legacyApiOptions, entraOptions)
    .MapBookingEndpoints();

app.MapCurrentUserEndpoints(entraOptions);
app.MapIdentityLinkEndpoints(entraOptions);
app.MapInvitationEndpoints(entraOptions);

// GET/POST /api/organizations: a registered user's organisations, and new
// ones. /api/organizations/{organizationId}/...: a registered, enabled
// member of that organisation, checked before any of these endpoints runs.
app.MapOrganizationEndpoints(entraOptions);

app.MapOrganizationGroup(entraOptions)
    .MapOrganizationDetailEndpoints()
    .MapOrganizationRoomEndpoints()
    .MapOrganizationBookingEndpoints()
    .MapMembershipEndpoints()
    .MapOrganizationInvitationEndpoints();

await app.InitialiseDatabaseAsync();

// Organizations:InitialLegacyOwnerUserId: the first Owner of the legacy
// organisation, named by whoever configures the API (see README).
await app.ApplyLegacyOwnerBootstrapAsync();

app.Run();

public partial class Program;