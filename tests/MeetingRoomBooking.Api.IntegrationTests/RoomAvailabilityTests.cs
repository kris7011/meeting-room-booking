using System.Net;
using System.Net.Http.Json;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class RoomAvailabilityTests
{
    private static readonly DateTimeOffset Start =
        new(2031, 3, 12, 9, 0, 0, TimeSpan.Zero);

    private sealed record AvailableRoom(int Id, string Name, int Capacity);

    private static string Url(
        DateTimeOffset start,
        DateTimeOffset end,
        int minCapacity = 1)
    {
        return "/api/rooms/available?startUtc=" +
            Uri.EscapeDataString(start.ToString("O")) +
            "&endUtc=" + Uri.EscapeDataString(end.ToString("O")) +
            "&minCapacity=" + minCapacity;
    }

    [Fact]
    public async Task AvailableRooms_ChooseSmallestFittingRoomFirst()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            Url(Start, Start.AddHours(1), minCapacity: 5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rooms = await response.Content.ReadFromJsonAsync<AvailableRoom[]>();
        Assert.NotNull(rooms);
        Assert.Equal(new[] { 8, 12 }, rooms.Select(room => room.Capacity));
    }

    [Fact]
    public async Task AvailableRooms_ExcludeOverlapsButAllowAdjacentTimes()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        using var booked = await client.PostAsJsonAsync(
            "/api/bookings",
            new {
                roomId = 2,
                title = "Existing booking",
                bookedBy = "Availability test",
                startUtc = Start,
                endUtc = Start.AddHours(1)
            });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);

        using var overlapping = await client.GetAsync(
            Url(Start.AddMinutes(30), Start.AddMinutes(90)));
        var available = await overlapping.Content
            .ReadFromJsonAsync<AvailableRoom[]>();
        Assert.NotNull(available);
        Assert.DoesNotContain(available, room => room.Id == 2);
        Assert.Equal(2, available.Length);

        using var nextHour = await client.GetAsync(
            Url(Start.AddHours(1), Start.AddHours(2)));
        var adjacent = await nextHour.Content
            .ReadFromJsonAsync<AvailableRoom[]>();
        Assert.NotNull(adjacent);
        Assert.Contains(adjacent, room => room.Id == 2);
    }

    [Fact]
    public async Task AvailableRooms_NormalizesTimezoneOffsets()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();
        using var booked = await client.PostAsJsonAsync(
            "/api/bookings",
            new {
                roomId = 1,
                title = "UTC meeting",
                bookedBy = "Availability test",
                startUtc = Start,
                endUtc = Start.AddHours(1)
            });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);

        using var result = await client.GetAsync(
            Url(Start.ToOffset(TimeSpan.FromHours(2)),
                Start.AddHours(1).ToOffset(TimeSpan.FromHours(2))));
        var rooms = await result.Content.ReadFromJsonAsync<AvailableRoom[]>();
        Assert.NotNull(rooms);
        Assert.DoesNotContain(rooms, room => room.Id == 1);
    }

    [Fact]
    public async Task AvailableRooms_RejectInvalidRangeAndCapacity()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = factory.CreateClient();

        using var invalidTime = await client.GetAsync(Url(Start, Start));
        Assert.Equal(HttpStatusCode.BadRequest, invalidTime.StatusCode);

        using var invalidCapacity = await client.GetAsync(
            Url(Start, Start.AddHours(1), 0));
        Assert.Equal(HttpStatusCode.BadRequest, invalidCapacity.StatusCode);

        using var missingTime = await client.GetAsync(
            "/api/rooms/available?minCapacity=1");
        Assert.Equal(HttpStatusCode.BadRequest, missingTime.StatusCode);
    }
}
