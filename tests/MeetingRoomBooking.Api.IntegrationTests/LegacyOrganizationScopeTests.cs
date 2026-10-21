using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeetingRoomBooking.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>
/// The original endpoints stay unauthenticated and unchanged in phase 1, but
/// only ever see the legacy organisation. Data of any other organisation must
/// be invisible and untouchable through them.
/// </summary>
public sealed class LegacyOrganizationScopeTests
{
    private const int OtherOrganizationId = 2;
    private const int OtherRoomId = 50;

    private static readonly DateTimeOffset Start =
        new(2031, 5, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LegacyEndpoints_IgnoreOtherOrganisations()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();
        var otherBookingId = await SeedOtherOrganizationAsync(factory);

        var rooms =
            await client.GetFromJsonAsync<JsonElement>("/api/rooms");
        Assert.DoesNotContain(
            rooms.EnumerateArray(),
            room => room.GetProperty("id").GetInt32() == OtherRoomId);

        var available =
            await client.GetFromJsonAsync<JsonElement>(
                $"/api/rooms/available?startUtc={Uri.EscapeDataString(Start.AddDays(1).ToString("O"))}" +
                $"&endUtc={Uri.EscapeDataString(Start.AddDays(1).AddHours(1).ToString("O"))}");
        Assert.DoesNotContain(
            available.EnumerateArray(),
            room => room.GetProperty("id").GetInt32() == OtherRoomId);

        var bookings =
            await client.GetFromJsonAsync<JsonElement>("/api/bookings");
        Assert.DoesNotContain(
            bookings.EnumerateArray(),
            booking => booking.GetProperty("id").GetInt32() == otherBookingId);

        await AssertProblemAsync(
            await client.GetAsync($"/api/bookings/{otherBookingId}"),
            HttpStatusCode.NotFound,
            "booking_not_found");

        await AssertProblemAsync(
            await client.PutAsJsonAsync(
                $"/api/bookings/{otherBookingId}",
                Request(1, Start.AddDays(2))),
            HttpStatusCode.NotFound,
            "booking_not_found");

        await AssertProblemAsync(
            await client.DeleteAsync($"/api/bookings/{otherBookingId}"),
            HttpStatusCode.NotFound,
            "booking_not_found");

        await AssertProblemAsync(
            await client.PostAsJsonAsync(
                "/api/bookings",
                Request(OtherRoomId, Start.AddDays(3))),
            HttpStatusCode.NotFound,
            "meeting_room_not_found");

        // A legacy booking cannot be moved into another organisation's room.
        var created =
            await client.PostAsJsonAsync(
                "/api/bookings",
                Request(1, Start.AddDays(4)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var legacyBookingId =
            (await created.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetInt32();

        await AssertProblemAsync(
            await client.PutAsJsonAsync(
                $"/api/bookings/{legacyBookingId}",
                Request(OtherRoomId, Start.AddDays(4))),
            HttpStatusCode.NotFound,
            "meeting_room_not_found");

        // The other organisation's booking is still there, unchanged.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var otherBooking =
            await context.Bookings.AsNoTracking()
                .SingleAsync(booking => booking.Id == otherBookingId);
        Assert.Equal(OtherOrganizationId, otherBooking.OrganizationId);
        Assert.Equal("Other organisation's meeting", otherBooking.Title);
    }

    [Fact]
    public async Task NewBookings_BelongToLegacyOrganisation()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                Request(2, Start));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id =
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetInt32();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var booking =
            await context.Bookings.AsNoTracking()
                .SingleAsync(item => item.Id == id);

        Assert.Equal(LegacyOrganization.Id, booking.OrganizationId);
        Assert.Null(booking.CreatedByUserId);
        Assert.Null(booking.UpdatedUtc);
        Assert.Null(booking.UpdatedByUserId);
    }

    [Fact]
    public async Task ResponseContracts_AreUnchanged()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        var rooms =
            await client.GetFromJsonAsync<JsonElement>("/api/rooms");
        Assert.Equal(
            ["id", "name", "capacity"],
            PropertyNames(rooms.EnumerateArray().First()));

        var created =
            await client.PostAsJsonAsync(
                "/api/bookings",
                Request(1, Start));
        var booking =
            await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["id", "roomId", "roomName", "title", "bookedBy", "startUtc", "endUtc", "createdUtc"],
            PropertyNames(booking));

        var bookings =
            await client.GetFromJsonAsync<JsonElement>("/api/bookings");
        Assert.Equal(
            PropertyNames(booking),
            PropertyNames(bookings.EnumerateArray().Single()));
    }

    [Fact]
    public async Task Phase1_RequiresNoAuthentication()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        Assert.Null(client.DefaultRequestHeaders.Authorization);

        var responses = new[]
        {
            await client.GetAsync("/health"),
            await client.GetAsync("/api/rooms"),
            await client.GetAsync("/api/bookings"),
            await client.PostAsJsonAsync("/api/bookings", Request(3, Start))
        };

        Assert.All(
            responses,
            response =>
            {
                Assert.True(
                    response.IsSuccessStatusCode,
                    $"{response.RequestMessage!.RequestUri} returned {response.StatusCode}.");
                Assert.False(response.Headers.Contains("WWW-Authenticate"));
            });
    }

    private static object Request(
        int roomId,
        DateTimeOffset start)
    {
        return new
        {
            roomId,
            title = "Scope test",
            bookedBy = "Scope test",
            startUtc = start,
            endUtc = start.AddHours(1)
        };
    }

    /// <summary>
    /// Inserts organisation 2 with a room and a booking straight into the
    /// database, as no endpoint can create them yet.
    /// </summary>
    private static async Task<int> SeedOtherOrganizationAsync(
        MeetingRoomBookingApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Organizations" ("Id", "PublicId", "Name", "IsLegacy", "CreatedUtc")
            VALUES ({0}, '6F1C1D4E-2B7A-4C55-9A0E-3D2B1F0A9C11', 'Acme', 0, 0)
            """,
            OtherOrganizationId);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "MeetingRooms" ("Id", "OrganizationId", "Name", "Capacity")
            VALUES ({0}, {1}, 'Hidden Room', 4)
            """,
            OtherRoomId,
            OtherOrganizationId);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Bookings"
                ("OrganizationId", "RoomId", "Title", "BookedBy", "StartUtc", "EndUtc", "CreatedUtc")
            VALUES ({0}, {1}, 'Other organisation''s meeting', 'Someone else', {2}, {3}, 0)
            """,
            OtherOrganizationId,
            OtherRoomId,
            Start.UtcTicks,
            Start.AddHours(1).UtcTicks);

        return await context.Bookings
            .Where(booking => booking.OrganizationId == OtherOrganizationId)
            .Select(booking => booking.Id)
            .SingleAsync();
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);

        var problem =
            await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            expectedCode,
            problem.GetProperty("code").GetString());
    }

    private static string[] PropertyNames(
        JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
    }
}
