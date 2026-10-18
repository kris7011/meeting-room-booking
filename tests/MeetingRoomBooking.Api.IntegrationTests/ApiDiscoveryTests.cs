using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingRoomBooking.Api.IntegrationTests;

public sealed class ApiDiscoveryTests(MeetingRoomBookingApiFactory factory)
    : IClassFixture<MeetingRoomBookingApiFactory>
{
    [Fact]
    public void ApiExplorer_DescribesBookingAndRoomEndpoints()
    {
        using var client = factory.CreateClient();

        var provider = factory.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>();

        var descriptions = provider.ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .ToArray();

        Assert.Contains(descriptions, item =>
            item.HttpMethod == "GET" && item.RelativePath == "api/rooms");
        Assert.Contains(descriptions, item =>
            item.HttpMethod == "GET" && item.RelativePath == "api/bookings");
        Assert.Contains(descriptions, item =>
            item.HttpMethod == "POST" && item.RelativePath == "api/bookings");
        Assert.Contains(descriptions, item =>
            item.HttpMethod == "PUT" && item.RelativePath == "api/bookings/{bookingId}");
        Assert.Contains(descriptions, item =>
            item.HttpMethod == "DELETE" && item.RelativePath == "api/bookings/{bookingId}");
    }
}
