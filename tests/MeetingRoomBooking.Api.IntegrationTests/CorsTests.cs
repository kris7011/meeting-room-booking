using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class CorsTests
{
    private const string ExpoWebOrigin =
        "http://localhost:8081";

    private static HttpClient CreateClient(
        MeetingRoomBookingApiFactory factory,
        string environment)
    {
        return factory
            .WithWebHostBuilder(
                builder =>
                    builder.UseEnvironment(environment))
            .CreateClient();
    }

    private static HttpRequestMessage BookingPreflight(
        string origin)
    {
        var request =
            new HttpRequestMessage(
                HttpMethod.Options,
                "/api/bookings");

        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        return request;
    }

    [Fact]
    public async Task Development_AllowsPreflightFromExpoWebOrigin()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = CreateClient(factory, "Development");

        using var request = BookingPreflight(ExpoWebOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            ExpoWebOrigin,
            Assert.Single(
                response.Headers.GetValues(
                    "Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Development_AllowsTheIdempotencyKeyHeader()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = CreateClient(factory, "Development");

        using var request = BookingPreflight(ExpoWebOrigin);
        request.Headers.Remove("Access-Control-Request-Headers");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,idempotency-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(
            "idempotency-key",
            string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Development_DoesNotAllowUnlistedOrigin()
    {
        using var factory = new MeetingRoomBookingApiFactory();
        using var client = CreateClient(factory, "Development");

        using var request = BookingPreflight("http://evil.example");
        using var response = await client.SendAsync(request);

        Assert.False(
            response.Headers.Contains(
                "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Production_SendsNoCorsHeaders()
    {
        // Anonymous demo mode is refused outside Development and Testing.
        using var factory = new MeetingRoomBookingApiFactory { AllowAnonymousLegacyApi = false };
        using var client = CreateClient(factory, "Production");

        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                "/health");
        request.Headers.Add("Origin", ExpoWebOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(
            response.Headers.Contains(
                "Access-Control-Allow-Origin"));
    }
}
