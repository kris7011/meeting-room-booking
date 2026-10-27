using System.Net;
using MeetingRoomBooking.Api.Features.Bookings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>
/// The security-relevant differences between Development and Production:
/// Swagger and plain HTTP only in Development, and no exception details in
/// any environment. Each check runs in both environments, so it shows the
/// difference (or the lack of one) it asserts. CORS is covered by
/// <see cref="CorsTests"/>, the refused anonymous demo mode by
/// <c>OrganizationScopedAccessTests</c>.
/// </summary>
public sealed class ProductionDefaultsTests
{
    private const string SecretInException =
        "Data Source=C:\\secret\\meeting-room-booking.db";

    // Anonymous demo mode is refused outside Development and Testing.
    private static MeetingRoomBookingApiFactory CreateFactory() =>
        new() { AllowAnonymousLegacyApi = false };

    private static HttpClient CreateClient(
        MeetingRoomBookingApiFactory factory,
        string environment,
        Action<IWebHostBuilder>? configure = null)
    {
        return factory
            .WithWebHostBuilder(
                builder =>
                {
                    builder.UseEnvironment(environment);
                    configure?.Invoke(builder);
                })
            .CreateClient(
                new()
                {
                    AllowAutoRedirect = false
                });
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task Swagger_IsServedInDevelopmentOnly(
        string environment,
        HttpStatusCode expected)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, environment);

        using var response =
            await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(expected, response.StatusCode);
    }

    /// <remarks>
    /// <c>UseExceptionHandler</c> runs in every environment and handles the
    /// exception before the developer exception page would, so Development
    /// does not show the details either. The exception is in the server log.
    /// </remarks>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnhandledException_IsAGenericProblemWithoutItsDetails(
        string environment)
    {
        using var factory = CreateFactory();
        using var client =
            CreateClient(
                factory,
                environment,
                builder =>
                    builder.ConfigureServices(
                        services =>
                        {
                            // Any unexpected failure inside a request, here
                            // with a connection string in its message.
                            services.RemoveAll<BookingService>();
                            services.AddScoped<BookingService>(
                                _ => throw new InvalidOperationException(SecretInException));
                        }));

        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                "/api/bookings");
        request.Headers.Add("Accept", "application/json");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("traceId", body);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("BookingService", body);
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.TemporaryRedirect)]
    public async Task HttpRequest_IsRedirectedToHttpsOutsideDevelopment(
        string environment,
        HttpStatusCode expected)
    {
        using var factory = CreateFactory();
        // The redirect needs to know the HTTPS port. Kestrel reports it when
        // it listens on HTTPS; the test server does not, so it is set here.
        // Without either, the middleware only logs a warning and redirects
        // nothing (see docs/PROJECT_WALKTHROUGH.md, Known limitations).
        using var client =
            CreateClient(
                factory,
                environment,
                builder => builder.UseSetting("https_port", "443"));

        using var response = await client.GetAsync("/health");

        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.TemporaryRedirect)
        {
            Assert.Equal(
                "https://localhost/health",
                response.Headers.Location?.ToString());
        }
    }

    /// <summary>
    /// The known limitation, pinned: a Production API that only listens on
    /// HTTP (for example behind a proxy that ends TLS) and has no HTTPS port
    /// configured answers plain HTTP instead of redirecting it.
    /// </summary>
    [Fact]
    public async Task HttpRequest_InProductionWithoutAKnownHttpsPort_IsNotRedirected()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, "Production");

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
