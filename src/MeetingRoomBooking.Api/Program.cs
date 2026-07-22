using MeetingRoomBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder =
    WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString(
        "BookingDatabase")
    ?? throw new InvalidOperationException(
        "The BookingDatabase connection string is missing.");

builder.Services.AddProblemDetails();

builder.Services.AddDbContext<BookingDbContext>(
    options =>
        options.UseSqlite(connectionString));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app =
    builder.Build();

app.UseExceptionHandler();

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
    .WithTags("System")
    .WithOpenApi();

await app.InitialiseDatabaseAsync();

app.Run();

public partial class Program;