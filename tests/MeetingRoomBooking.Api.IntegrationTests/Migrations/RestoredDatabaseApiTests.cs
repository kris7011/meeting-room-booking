using System.Net;
using MeetingRoomBooking.Api.Data;
using MeetingRoomBooking.Api.IntegrationTests.Authentication;
using MeetingRoomBooking.Api.IntegrationTests.Organizations;
using Microsoft.AspNetCore.Mvc.Testing;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;
using static MeetingRoomBooking.Api.IntegrationTests.Organizations.TenantScenario;

namespace MeetingRoomBooking.Api.IntegrationTests.Migrations;

/// <summary>
/// The Phase 4A recovery path end to end: back up a database while the API
/// uses it, restore it to a new file and start a second API on that file.
/// The tenant data must survive, sign-in and organisation access control
/// must behave as before, and the original must not change.
/// </summary>
public sealed class RestoredDatabaseApiTests
{
    private const string LegacyBookingTitle = "Legacy board meeting";
    private const string OtherBookingTitle = "Other Org planning";

    [Fact]
    public async Task RestoredDatabase_KeepsTenantData_AndTheApiStillEnforcesAccess()
    {
        using var scenario = await TenantScenario.CreateAsync();

        await scenario.CreateBookingAsync(
            LegacyPrefix, scenario.MemberToken, scenario.BookingRequest(1, title: LegacyBookingTitle));
        await scenario.CreateBookingAsync(
            scenario.OtherPrefix, scenario.OtherOwnerToken, scenario.BookingRequest(scenario.OtherRoomId, title: OtherBookingTitle));

        var workDirectory = Path.Combine(Path.GetTempPath(), $"meeting-room-booking-tests-restore-{Guid.NewGuid():N}");

        try
        {
            var original = new MigrationTestDatabase.ReadOnlyFile(scenario.DatabasePath);
            var originalBefore = await DumpAsync(original);

            // The API is still running, with recent commits possibly only in its WAL.
            var backup = await DatabasePortability.BackupAsync(scenario.DatabasePath, workDirectory);
            var restoredPath = Path.Combine(workDirectory, "restored", "room-booking.db");
            await DatabasePortability.RestoreToNewAsync(backup, restoredPath);

            // Every table, migration history included, row for row.
            Assert.Equal(originalBefore, await DumpAsync(new MigrationTestDatabase.ReadOnlyFile(restoredPath)));
            Assert.Equal(2L, Rows(originalBefore, "Organizations"));
            Assert.Equal(2L, Rows(originalBefore, "Bookings"));
            Assert.True(Rows(originalBefore, "MeetingRooms") >= 2);
            Assert.Equal(4L, Rows(originalBefore, "Memberships"));

            using var restoredFactory = new MeetingRoomBookingApiFactory
            {
                AllowAnonymousLegacyApi = false,
                DatabasePath = restoredPath
            };
            using var restoredApi = restoredFactory.WithWebHostBuilder(builder => builder.UseEntraTestTokens());

            // Members still see their own organisation's bookings.
            Assert.Contains(LegacyBookingTitle, await BookingTitlesAsync(restoredApi, LegacyPrefix, scenario.OwnerToken));
            Assert.Contains(LegacyBookingTitle, await BookingTitlesAsync(restoredApi, LegacyPrefix, scenario.MemberToken));
            Assert.Equal(
                [OtherBookingTitle],
                await BookingTitlesAsync(restoredApi, scenario.OtherPrefix, scenario.OtherOwnerToken));

            // Anonymous callers and invalid tokens are refused.
            foreach (var path in new[] { $"{LegacyPrefix}/bookings", "/api/rooms", "/api/me" })
            {
                using var anonymous = await SendAsync(restoredApi, HttpMethod.Get, path, token: null);
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

                using var invalid = await SendAsync(restoredApi, HttpMethod.Get, path, "not-a-jwt");
                Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
            }

            // Non-members cannot see either organisation.
            using (var stranger = await SendAsync(restoredApi, HttpMethod.Get, $"{scenario.OtherPrefix}/bookings", scenario.OwnerToken))
            {
                await AssertProblemAsync(stranger, HttpStatusCode.NotFound, "organization_not_found");
            }

            using (var outsider = await SendAsync(restoredApi, HttpMethod.Get, $"{LegacyPrefix}/bookings", scenario.OutsiderToken))
            {
                await AssertProblemAsync(outsider, HttpStatusCode.NotFound, "organization_not_found");
            }

            // Roles survived: a Member may not add rooms, the Owner may.
            using (var memberRoom = await SendAsync(
                       restoredApi, HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.MemberToken, new { name = "Restored Room", capacity = 4 }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, memberRoom.StatusCode);
            }

            using (var ownerRoom = await SendAsync(
                       restoredApi, HttpMethod.Post, $"{LegacyPrefix}/rooms", scenario.OwnerToken, new { name = "Restored Room", capacity = 4 }))
            {
                Assert.Equal(HttpStatusCode.Created, ownerRoom.StatusCode);
            }

            // Nothing to migrate, so the restored API took no start-up backup.
            Assert.False(Directory.Exists($"{restoredPath}-backups"));

            // Writes to the restored copy never reach the original.
            Assert.Equal(originalBefore, await DumpAsync(original));
        }
        finally
        {
            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, recursive: true);
            }
        }
    }

    private static async Task<string[]> BookingTitlesAsync(
        WebApplicationFactory<Program> api,
        string prefix,
        string token)
    {
        using var response = await SendAsync(api, HttpMethod.Get, $"{prefix}/bookings", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await JsonAsync(response))
            .EnumerateArray()
            .Select(booking => booking.GetProperty("title").GetString()!)
            .ToArray();
    }

    /// <summary>Every user table as "table: row count" followed by its rows in rowid order.</summary>
    private static async Task<IReadOnlyList<string>> DumpAsync(
        MigrationTestDatabase.ReadOnlyFile database)
    {
        var tables =
            (await database.QueryAsync(
                """SELECT "name" FROM sqlite_master WHERE "type" = 'table' AND "name" NOT LIKE 'sqlite_%' ORDER BY "name" """))
            .Select(row => (string)row[0]!)
            .ToArray();

        var dump = new List<string>();

        foreach (var table in tables)
        {
            var rows = await database.QueryAsync($"""SELECT * FROM "{table}" ORDER BY rowid""");
            dump.Add($"{table}: {rows.Count}");
            dump.AddRange(rows.Select(row => string.Join("|", row.Select(value => value?.ToString() ?? "NULL"))));
        }

        return dump;
    }

    private static long Rows(
        IReadOnlyList<string> dump,
        string table) =>
        long.Parse(dump.Single(line => line.StartsWith($"{table}: ", StringComparison.Ordinal))[(table.Length + 2)..]);
}
