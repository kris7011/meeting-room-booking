using System.Net;
using System.Net.Http.Json;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class BookingEndpointTests
{
    private static readonly DateTimeOffset BaseStart =
        new(
            year: 2030,
            month: 1,
            day: 10,
            hour: 9,
            minute: 0,
            second: 0,
            offset: TimeSpan.Zero);

    [Fact]
    public async Task GetRooms_ReturnsSeededRooms()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var response =
            await client.GetAsync(
                "/api/rooms");

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var rooms =
            await response.Content
                .ReadFromJsonAsync<
                    MeetingRoomApiResponse[]>();

        Assert.NotNull(rooms);

        Assert.Collection(
            rooms,
            room =>
            {
                Assert.Equal(
                    3,
                    room.Id);

                Assert.Equal(
                    "Board Room",
                    room.Name);

                Assert.Equal(
                    12,
                    room.Capacity);
            },
            room =>
            {
                Assert.Equal(
                    2,
                    room.Id);

                Assert.Equal(
                    "Collaboration Room",
                    room.Name);

                Assert.Equal(
                    8,
                    room.Capacity);
            },
            room =>
            {
                Assert.Equal(
                    1,
                    room.Id);

                Assert.Equal(
                    "Focus Room",
                    room.Name);

                Assert.Equal(
                    4,
                    room.Capacity);
            });
    }

    [Fact]
    public async Task CreateBooking_ValidRequest_PersistsBooking()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                roomId: 1,
                title: "Sprint planning",
                startUtc: BaseStart,
                endUtc:
                    BaseStart.AddHours(1));

        Assert.NotEqual(
            0,
            created.Id);

        Assert.Equal(
            1,
            created.RoomId);

        Assert.Equal(
            "Focus Room",
            created.RoomName);

        Assert.Equal(
            "Sprint planning",
            created.Title);

        Assert.Equal(
            "Kris Larsen",
            created.BookedBy);

        var getResponse =
            await client.GetAsync(
                $"/api/bookings/{created.Id}");

        Assert.Equal(
            HttpStatusCode.OK,
            getResponse.StatusCode);

        var persisted =
            await getResponse.Content
                .ReadFromJsonAsync<
                    BookingApiResponse>();

        Assert.NotNull(persisted);

        Assert.Equal(
            created.Id,
            persisted.Id);

        var listResponse =
            await client.GetAsync(
                "/api/bookings");

        Assert.Equal(
            HttpStatusCode.OK,
            listResponse.StatusCode);

        var bookings =
            await listResponse.Content
                .ReadFromJsonAsync<
                    BookingApiResponse[]>();

        Assert.NotNull(bookings);

        Assert.Contains(
            bookings,
            booking =>
                booking.Id == created.Id);
    }

    [Fact]
    public async Task CreateBooking_OverlappingTimeForSameRoom_ReturnsConflict()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        await CreateBookingAsync(
            client,
            roomId: 1,
            title: "First meeting",
            startUtc: BaseStart,
            endUtc:
                BaseStart.AddHours(1));

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 1,
                    title: "Overlapping meeting",
                    startUtc:
                        BaseStart.AddMinutes(30),
                    endUtc:
                        BaseStart.AddMinutes(90)));

        Assert.Equal(
            HttpStatusCode.Conflict,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<
                    ApiProblemResponse>();

        Assert.NotNull(problem);

        Assert.Equal(
            "booking_overlap",
            problem.Code);

        Assert.Equal(
            409,
            problem.Status);

        Assert.Contains(
            "already booked",
            problem.Detail);
    }

    [Fact]
    public async Task CreateBooking_SameTimeForDifferentRoom_ReturnsCreated()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        await CreateBookingAsync(
            client,
            roomId: 1,
            title: "Focus room meeting",
            startUtc: BaseStart,
            endUtc:
                BaseStart.AddHours(1));

        var created =
            await CreateBookingAsync(
                client,
                roomId: 2,
                title: "Collaboration meeting",
                startUtc: BaseStart,
                endUtc:
                    BaseStart.AddHours(1));

        Assert.Equal(
            2,
            created.RoomId);

        Assert.Equal(
            "Collaboration Room",
            created.RoomName);
    }

    [Fact]
    public async Task CreateBooking_AdjacentTime_ReturnsCreated()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        await CreateBookingAsync(
            client,
            roomId: 1,
            title: "Morning meeting",
            startUtc: BaseStart,
            endUtc:
                BaseStart.AddHours(1));

        var created =
            await CreateBookingAsync(
                client,
                roomId: 1,
                title: "Next meeting",
                startUtc:
                    BaseStart.AddHours(1),
                endUtc:
                    BaseStart.AddHours(2));

        Assert.Equal(
            BaseStart.AddHours(1),
            created.StartUtc);

        Assert.Equal(
            BaseStart.AddHours(2),
            created.EndUtc);
    }

    [Fact]
    public async Task CreateBooking_InvalidTimeRange_ReturnsValidationProblem()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 1,
                    title: "Invalid meeting",
                    startUtc: BaseStart,
                    endUtc:
                        BaseStart.AddMinutes(-30)));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var validationProblem =
            await response.Content
                .ReadFromJsonAsync<
                    ValidationProblemApiResponse>();

        Assert.NotNull(validationProblem);

        Assert.True(
            validationProblem.Errors.TryGetValue(
                "endUtc",
                out var endUtcErrors));

        Assert.Contains(
            "The end time must be later than the start time.",
            endUtcErrors);
    }

    [Fact]
    public async Task CreateBooking_UnknownRoom_ReturnsNotFound()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 999,
                    title: "Unknown room",
                    startUtc: BaseStart,
                    endUtc:
                        BaseStart.AddHours(1)));

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<
                    ApiProblemResponse>();

        Assert.NotNull(problem);

        Assert.Equal(
            "meeting_room_not_found",
            problem.Code);
    }

    private static async Task<BookingApiResponse>
        CreateBookingAsync(
            HttpClient client,
            int roomId,
            string title,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc)
    {
        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId,
                    title,
                    startUtc,
                    endUtc));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var booking =
            await response.Content
                .ReadFromJsonAsync<
                    BookingApiResponse>();

        Assert.NotNull(booking);

        Assert.Equal(
            $"/api/bookings/{booking.Id}",
            response.Headers.Location
                ?.OriginalString);

        return booking;
    }

    private static object CreateRequest(
        int roomId,
        string title,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        return new
        {
            roomId,
            title,
            bookedBy =
                "Kris Larsen",
            startUtc,
            endUtc
        };
    }
}

internal sealed record MeetingRoomApiResponse(
    int Id,
    string Name,
    int Capacity);

internal sealed record BookingApiResponse(
    int Id,
    int RoomId,
    string RoomName,
    string Title,
    string BookedBy,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset CreatedUtc);

internal sealed record ApiProblemResponse(
    string Title,
    int Status,
    string Detail,
    string Code);

internal sealed record ValidationProblemApiResponse(
    Dictionary<string, string[]> Errors);