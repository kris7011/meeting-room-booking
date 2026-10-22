using Microsoft.AspNetCore.Hosting;

namespace MeetingRoomBooking.Api.IntegrationTests.Authentication;

public sealed class EntraAuthenticationOptionsTests
{
    [Theory]
    [InlineData("Entra:ApiClientId", "my-api", "ApiClientId")]
    [InlineData("Entra:Instance", "http://login.microsoftonline.com", "Instance")]
    [InlineData("Entra:RequiredScope", "a b", "RequiredScope")]
    [InlineData("Entra:AllowedTenantIds:0", "contoso.com", "AllowedTenantIds")]
    public void InvalidSettings_StopTheApiFromStarting(
        string key,
        string value,
        string expectedSetting)
    {
        using var baseFactory = new MeetingRoomBookingApiFactory();
        using var factory =
            baseFactory.WithWebHostBuilder(
                builder =>
                {
                    builder.UseSetting("Entra:ApiClientId", EntraTestTokens.ApiClientId);
                    builder.UseSetting(key, value);
                });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains($"Entra:{expectedSetting}", error.Message);
    }
}
