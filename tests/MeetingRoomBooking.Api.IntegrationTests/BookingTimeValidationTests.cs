using System.Net;
using System.Net.Http.Json;

namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>
/// A booking must start after the server's current time, both when it is
/// created and when its period is changed. The server's clock is the
/// factory's <see cref="TestTimeProvider"/>, set to exact instants here.
/// </summary>
public sealed class BookingTimeValidationTests
{
    private const string StartNotInFutureMessage =
        "The start time must be later than the current time.";

    private static readonly DateTimeOffset Now =
        new(
            year: 2030,
            month: 3,
            day: 4,
            hour: 10,
            minute: 0,
            second: 0,
            offset: TimeSpan.Zero);

    public static TheoryData<int, string> StartsNotInTheFuture =>
        new()
        {
            { -24 * 60, "yesterday" },
            { -60, "an hour ago" },
            { -1, "a minute ago" },
            { 0, "exactly now" }
        };

    [Theory]
    [MemberData(nameof(StartsNotInTheFuture))]
    public async Task CreateBooking_StartNotInTheFuture_ReturnsValidationProblemOnStartUtc(
        int minutesFromNow,
        string description)
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var start =
            Now.AddMinutes(minutesFromNow);

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 1,
                    startUtc: start,
                    endUtc: start.AddHours(1)));

        await AssertStartNotInFutureAsync(
            response,
            description);

        await AssertNoBookingsAsync(
            client);
    }

    [Fact]
    public async Task CreateBooking_StartedMeetingThatHasNotEnded_ReturnsValidationProblem()
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 1,
                    startUtc: Now.AddMinutes(-30),
                    endUtc: Now.AddMinutes(30)));

        await AssertStartNotInFutureAsync(
            response,
            "started half an hour ago");
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(60)]
    [InlineData(60 * 24 * 365)]
    public async Task CreateBooking_StartInTheFuture_IsCreated(
        double minutesFromNow)
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var start =
            Now.AddMinutes(minutesFromNow);

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 1,
                    startUtc: start,
                    endUtc: start.AddHours(1)));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var created =
            await response.Content
                .ReadFromJsonAsync<
                    BookingApiResponse>();

        Assert.NotNull(
            created);

        Assert.Equal(
            start,
            created.StartUtc);

        // CreatedUtc comes from the same clock as the rule.
        Assert.Equal(
            Now,
            created.CreatedUtc);
    }

    [Fact]
    public async Task CreateBooking_PastStartAndUnknownRoom_ReturnsValidationProblemFirst()
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 999,
                    startUtc: Now.AddHours(-1),
                    endUtc: Now));

        await AssertStartNotInFutureAsync(
            response,
            "unknown room in the past");
    }

    [Fact]
    public async Task CreateBooking_OtherValidationErrors_AreStillReported()
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                new
                {
                    roomId = 1,
                    title = "",
                    bookedBy = "Kris Larsen",
                    startUtc = Now.AddHours(-1),
                    endUtc = Now.AddHours(-2)
                });

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<
                    ValidationProblemApiResponse>();

        Assert.NotNull(
            problem);

        Assert.True(
            problem.Errors.ContainsKey("title"));

        Assert.True(
            problem.Errors.ContainsKey("endUtc"));
    }

    // 25 October 2026: Copenhagen goes from summer time (UTC+2) back to
    // winter time (UTC+1) at 03:00 local time, which is 01:00 UTC. Local
    // 02:00-02:59 happens twice. The server compares instants, not the local
    // clock time a client sends.
    [Theory]
    [InlineData("2026-10-25T02:30:00+01:00", false, "equal to now (second 02:30, winter time)")]
    [InlineData("2026-10-25T02:45:00+02:00", false, "first 02:45 (summer time), already past")]
    [InlineData("2026-10-25T02:45:00+01:00", true, "second 02:45 (winter time), still ahead")]
    [InlineData("2026-10-25T01:31:00+00:00", true, "the same kind of instant sent in UTC")]
    [InlineData("2026-10-25T07:00:00+05:30", false, "India, 01:30 UTC")]
    [InlineData("2026-10-24T21:31:00-04:00", true, "New York, previous calendar day, 01:31 UTC")]
    public async Task CreateBooking_StartWithOffset_IsComparedAsAnInstant(
        string start,
        bool accepted,
        string description)
    {
        var nowUtc =
            DateTimeOffset.Parse("2026-10-25T01:30:00Z");

        using var factory =
            CreateFactoryAt(nowUtc);

        using var client =
            factory.CreateClient();

        var startUtc =
            DateTimeOffset.Parse(start);

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 1,
                    startUtc: startUtc,
                    endUtc: startUtc.AddHours(1)));

        if (!accepted)
        {
            await AssertStartNotInFutureAsync(
                response,
                description);

            return;
        }

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{description}: expected 201, got {(int)response.StatusCode}.");

        var created =
            await response.Content
                .ReadFromJsonAsync<
                    BookingApiResponse>();

        Assert.NotNull(
            created);

        // Stored and returned in UTC.
        Assert.Equal(
            TimeSpan.Zero,
            created.StartUtc.Offset);

        Assert.Equal(
            startUtc.UtcDateTime,
            created.StartUtc.UtcDateTime);
    }

    [Theory]
    [MemberData(nameof(StartsNotInTheFuture))]
    public async Task UpdateBooking_MoveToStartNotInTheFuture_ReturnsValidationProblemAndKeepsBooking(
        int minutesFromNow,
        string description)
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                startUtc: Now.AddDays(1));

        var newStart =
            Now.AddMinutes(minutesFromNow);

        var response =
            await client.PutAsJsonAsync(
                $"/api/bookings/{created.Id}",
                CreateRequest(
                    roomId: 1,
                    startUtc: newStart,
                    endUtc: newStart.AddHours(1),
                    title: "Moved"));

        await AssertStartNotInFutureAsync(
            response,
            description);

        var persisted =
            await GetBookingAsync(
                client,
                created.Id);

        Assert.Equal(
            created.StartUtc,
            persisted.StartUtc);

        Assert.Equal(
            created.Title,
            persisted.Title);
    }

    [Fact]
    public async Task UpdateBooking_MoveToAnotherFutureTime_IsSaved()
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                startUtc: Now.AddDays(1));

        var response =
            await client.PutAsJsonAsync(
                $"/api/bookings/{created.Id}",
                CreateRequest(
                    roomId: 1,
                    startUtc: Now.AddMinutes(30),
                    endUtc: Now.AddMinutes(90)));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var persisted =
            await GetBookingAsync(
                client,
                created.Id);

        Assert.Equal(
            Now.AddMinutes(30),
            persisted.StartUtc);
    }

    [Fact]
    public async Task UpdateBooking_ChangeEndOfStartedBooking_ReturnsValidationProblem()
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                startUtc: Now.AddHours(1));

        factory.Clock.UtcNow =
            Now.AddHours(1.5);

        var response =
            await client.PutAsJsonAsync(
                $"/api/bookings/{created.Id}",
                CreateRequest(
                    roomId: 1,
                    startUtc: created.StartUtc,
                    endUtc: created.EndUtc.AddHours(1)));

        await AssertStartNotInFutureAsync(
            response,
            "extending a booking that has started");
    }

    [Fact]
    public async Task PastBooking_CanStillBeReadAndCorrectedWithoutChangingItsPeriod()
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var created =
            await CreateBookingAsync(
                client,
                startUtc: Now.AddHours(1));

        // The meeting has taken place.
        factory.Clock.UtcNow =
            Now.AddDays(7);

        var byId =
            await GetBookingAsync(
                client,
                created.Id);

        Assert.Equal(
            created.StartUtc,
            byId.StartUtc);

        var all =
            await client.GetFromJsonAsync<
                BookingApiResponse[]>(
                "/api/bookings");

        Assert.NotNull(
            all);

        Assert.Contains(
            all,
            booking => booking.Id == created.Id);

        // The same period sent with another offset is still the same period.
        var response =
            await client.PutAsJsonAsync(
                $"/api/bookings/{created.Id}",
                CreateRequest(
                    roomId: 1,
                    startUtc:
                        created.StartUtc.ToOffset(TimeSpan.FromHours(1)),
                    endUtc:
                        created.EndUtc.ToOffset(TimeSpan.FromHours(1)),
                    title: "Corrected title"));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var corrected =
            await GetBookingAsync(
                client,
                created.Id);

        Assert.Equal(
            "Corrected title",
            corrected.Title);

        Assert.Equal(
            created.StartUtc,
            corrected.StartUtc);
    }

    [Fact]
    public async Task UpdateBooking_UnknownBooking_IsStillNotFound()
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        var response =
            await client.PutAsJsonAsync(
                "/api/bookings/999",
                CreateRequest(
                    roomId: 1,
                    startUtc: Now.AddHours(-1),
                    endUtc: Now));

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);
    }

    [Fact]
    public async Task CreateBooking_FutureOverlap_IsStillAConflict()
    {
        using var factory =
            CreateFactoryAt(Now);

        using var client =
            factory.CreateClient();

        await CreateBookingAsync(
            client,
            startUtc: Now.AddHours(1));

        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 1,
                    startUtc: Now.AddHours(1.5),
                    endUtc: Now.AddHours(2.5)));

        Assert.Equal(
            HttpStatusCode.Conflict,
            response.StatusCode);
    }

    private static MeetingRoomBookingApiFactory CreateFactoryAt(
        DateTimeOffset utcNow)
    {
        var factory =
            new MeetingRoomBookingApiFactory();

        factory.Clock.UtcNow =
            utcNow;

        return factory;
    }

    private static async Task AssertStartNotInFutureAsync(
        HttpResponseMessage response,
        string description)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{description}: expected 400, got {(int)response.StatusCode}.");

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem =
            await response.Content
                .ReadFromJsonAsync<
                    ValidationProblemApiResponse>();

        Assert.NotNull(
            problem);

        Assert.True(
            problem.Errors.TryGetValue(
                "startUtc",
                out var startUtcErrors),
            $"{description}: no startUtc error.");

        Assert.Equal(
            [StartNotInFutureMessage],
            startUtcErrors);
    }

    private static async Task AssertNoBookingsAsync(
        HttpClient client)
    {
        var bookings =
            await client.GetFromJsonAsync<
                BookingApiResponse[]>(
                "/api/bookings");

        Assert.NotNull(
            bookings);

        Assert.Empty(
            bookings);
    }

    private static async Task<BookingApiResponse> CreateBookingAsync(
        HttpClient client,
        DateTimeOffset startUtc)
    {
        var response =
            await client.PostAsJsonAsync(
                "/api/bookings",
                CreateRequest(
                    roomId: 1,
                    startUtc: startUtc,
                    endUtc: startUtc.AddHours(1)));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var created =
            await response.Content
                .ReadFromJsonAsync<
                    BookingApiResponse>();

        Assert.NotNull(
            created);

        return created;
    }

    private static async Task<BookingApiResponse> GetBookingAsync(
        HttpClient client,
        int bookingId)
    {
        var booking =
            await client.GetFromJsonAsync<
                BookingApiResponse>(
                $"/api/bookings/{bookingId}");

        Assert.NotNull(
            booking);

        return booking;
    }

    private static object CreateRequest(
        int roomId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title = "Planning")
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
