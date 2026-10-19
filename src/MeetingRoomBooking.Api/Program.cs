using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.Features.Bookings;
using MeetingRoomBooking.Api.Features.Rooms;
using Microsoft.EntityFrameworkCore;

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
                        .WithHeaders("Accept", "Content-Type")));
}

builder.Services.AddDbContext<BookingDbContext>(
    options =>
        options.UseSqlite(connectionString));

builder.Services.AddScoped<BookingService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app =
    builder.Build();

app.UseExceptionHandler();

if (corsAllowedOrigins.Length > 0)
{
    app.UseCors(DevelopmentClientsCorsPolicy);
}

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
        () =>
            Results.Ok(
                new
                {
                    status = "Healthy",
                    utcTimestamp =
                        DateTimeOffset.UtcNow
                }))
    .WithName("GetHealth")
    .WithTags("System");

app.MapRoomEndpoints();
app.MapBookingEndpoints();

await app.InitialiseDatabaseAsync();

app.Run();

public partial class Program;