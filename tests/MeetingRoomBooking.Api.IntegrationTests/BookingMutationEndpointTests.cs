using System.Net;
using System.Net.Http.Json;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class BookingMutationEndpointTests
{
    private static readonly DateTimeOffset BaseStart =
        new(
            year: 2030,
            month: 2,
            day: 15,
            hour: 9,
            minute: 0,
            second: 0,
            offset: TimeSpan.Zero);

    [Fact]
    public async Task UpdateBooking_ValidRequest_UpdatesPersistedBooking()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                roomId: 1,
                title: "Original meeting",
                startUtc: BaseStart,
                endUtc:
                    BaseStart.AddHours(1));

        var updateResponse =
            await client.PutAsJsonAsync(
                $"/api/bookings/{created.Id}",
                CreateRequest(
                    roomId: 2,
                    title: "Updated meeting",
                    startUtc:
                        BaseStart.AddHours(2),
                    endUtc:
                        BaseStart.AddHours(3)));

        Assert.Equal(
            HttpStatusCode.OK,
            updateResponse.StatusCode);

        var updated =
            await updateResponse.Content
                .ReadFromJsonAsync<
                    BookingApiResponse>();

        Assert.NotNull(
            updated);

        Assert.Equal(
            created.Id,
            updated.Id);

        Assert.Equal(
            2,
            updated.RoomId);

        Assert.Equal(
            "Collaboration Room",
            updated.RoomName);

        Assert.Equal(
            "Updated meeting",
            updated.Title);

        Assert.Equal(
            BaseStart.AddHours(2),
            updated.StartUtc);

        Assert.Equal(
            BaseStart.AddHours(3),
            updated.EndUtc);

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

        Assert.NotNull(
            persisted);

        Assert.Equal(
            "Updated meeting",
            persisted.Title);

        Assert.Equal(
            2,
            persisted.RoomId);
    }

    [Fact]
    public async Task UpdateBooking_UnchangedTime_DoesNotConflictWithItself()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                roomId: 1,
                title: "Original title",
                startUtc: BaseStart,
                endUtc:
                    BaseStart.AddHours(1));

        var response =
            await client.PutAsJsonAsync(
                $"/api/bookings/{created.Id}",
                CreateRequest(
                    roomId: 1,
                    title: "Changed title",
                    startUtc: BaseStart,
                    endUtc:
                        BaseStart.AddHours(1)));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var updated =
            await response.Content
                .ReadFromJsonAsync<
                    BookingApiResponse>();

        Assert.NotNull(
            updated);

        Assert.Equal(
            "Changed title",
            updated.Title);
    }

    [Fact]
    public async Task UpdateBooking_OverlappingAnotherBooking_ReturnsConflict()
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

        var second =
            await CreateBookingAsync(
                client,
                roomId: 1,
                title: "Second meeting",
                startUtc:
                    BaseStart.AddHours(2),
                endUtc:
                    BaseStart.AddHours(3));

        var response =
            await client.PutAsJsonAsync(
                $"/api/bookings/{second.Id}",
                CreateRequest(
                    roomId: 1,
                    title: "Conflicting update",
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

        Assert.NotNull(
            problem);

        Assert.Equal(
            "booking_overlap",
            problem.Code);
    }

    [Fact]
    public async Task UpdateBooking_UnknownRoom_ReturnsNotFound()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                roomId: 1,
                title: "Existing meeting",
                startUtc: BaseStart,
                endUtc:
                    BaseStart.AddHours(1));

        var response =
            await client.PutAsJsonAsync(
                $"/api/bookings/{created.Id}",
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

        Assert.NotNull(
            problem);

        Assert.Equal(
            "meeting_room_not_found",
            problem.Code);
    }

    [Fact]
    public async Task UpdateBooking_UnknownBooking_ReturnsNotFound()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var response =
            await client.PutAsJsonAsync(
                "/api/bookings/999",
                CreateRequest(
                    roomId: 1,
                    title: "Missing booking",
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

        Assert.NotNull(
            problem);

        Assert.Equal(
            "booking_not_found",
            problem.Code);
    }

    [Fact]
    public async Task DeleteBooking_ExistingBooking_RemovesBooking()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                roomId: 1,
                title: "Meeting to delete",
                startUtc: BaseStart,
                endUtc:
                    BaseStart.AddHours(1));

        var deleteResponse =
            await client.DeleteAsync(
                $"/api/bookings/{created.Id}");

        Assert.Equal(
            HttpStatusCode.NoContent,
            deleteResponse.StatusCode);

        var getResponse =
            await client.GetAsync(
                $"/api/bookings/{created.Id}");

        Assert.Equal(
            HttpStatusCode.NotFound,
            getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteBooking_UnknownBooking_ReturnsNotFound()
    {
        using var factory =
            new MeetingRoomBookingApiFactory();

        using var client =
            factory.CreateClient();

        var response =
            await client.DeleteAsync(
                "/api/bookings/999");

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<
                    ApiProblemResponse>();

        Assert.NotNull(
            problem);

        Assert.Equal(
            "booking_not_found",
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

        Assert.NotNull(
            booking);

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