using MeetingRoomBooking.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>
/// The API migrates its database at start-up, so a test host pointed at the
/// development database would migrate it. These checks keep every test host
/// on its own temp file.
/// </summary>
public sealed class TestDatabaseIsolationTests(
    MeetingRoomBookingApiFactory factory)
    : IClassFixture<MeetingRoomBookingApiFactory>
{
    [Fact]
    public void TestHost_UsesItsOwnTempDatabase()
    {
        using var scope = factory.Services.CreateScope();

        var context =
            scope.ServiceProvider.GetRequiredService<BookingDbContext>();

        var databaseFile =
            DatabaseBackup.ResolveDatabaseFile(
                context.Database.GetConnectionString());

        Assert.Equal(
            Path.GetFullPath(factory.DatabasePath),
            databaseFile);

        TestDatabaseGuard.EnsureIsolated(databaseFile!);
    }

    [Fact]
    public void TestHost_NeverSeesTheDevelopmentConnectionString()
    {
        var configured =
            factory.Services
                .GetRequiredService<IConfiguration>()
                .GetConnectionString("BookingDatabase");

        Assert.Equal(
            factory.ConnectionString,
            configured);
    }
}
