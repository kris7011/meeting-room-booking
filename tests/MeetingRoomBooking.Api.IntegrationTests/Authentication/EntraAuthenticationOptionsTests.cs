using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeetingRoomBooking.Api.IntegrationTests.Authentication;

public sealed class EntraAuthenticationOptionsTests
{
    [Theory]
    [InlineData("Entra:ApiClientId", "my-api", "ApiClientId")]
    [InlineData("Entra:Instance", "http://login.microsoftonline.com", "Instance")]
    [InlineData("Entra:RequiredScope", "a b", "RequiredScope")]
    [InlineData("Entra:AllowedTenantIds:0", "contoso.com", "AllowedTenantIds")]
    [InlineData("Entra:AllowedTenantIds:0", EntraTestTokens.PersonalAccountsTenantId, "AllowPersonalAccounts")]
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

    [Theory]
    [InlineData(null, "https://login.microsoftonline.com/common/v2.0")]
    [InlineData("true", "https://login.microsoftonline.com/common/v2.0")]
    [InlineData("false", "https://login.microsoftonline.com/organizations/v2.0")]
    public void SigningKeys_ComeFromCommon_UnlessPersonalAccountsAreOff(
        string? allowPersonalAccounts,
        string expectedAuthority)
    {
        using var baseFactory = new MeetingRoomBookingApiFactory();
        using var factory =
            baseFactory.WithWebHostBuilder(
                builder =>
                {
                    builder.UseSetting("Entra:ApiClientId", EntraTestTokens.ApiClientId);

                    if (allowPersonalAccounts is not null)
                    {
                        builder.UseSetting("Entra:AllowPersonalAccounts", allowPersonalAccounts);
                    }
                });

        var jwt =
            factory.Services
                .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(expectedAuthority, jwt.Authority);
    }
}
