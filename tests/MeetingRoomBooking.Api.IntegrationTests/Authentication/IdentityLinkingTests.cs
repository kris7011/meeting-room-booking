using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.SignedInRequests;

namespace MeetingRoomBooking.Api.IntegrationTests.Authentication;

/// <summary>
/// Explicit linking of a second sign-in identity: started by the user, the
/// code claimed by the new identity, confirmed by the user again. Nothing is
/// linked before the confirmation, and never because e-mails match.
/// </summary>
public sealed class IdentityLinkingTests
    : IDisposable
{
    private readonly MeetingRoomBookingApiFactory baseFactory =
        new();

    private readonly WebApplicationFactory<Program> factory;

    /// <summary>The account: a Microsoft sign-in with organisations and data.</summary>
    private readonly string accountToken =
        TokenFor("oid-account", "kris@example.com", name: "Kris Account");

    /// <summary>The identity to add: an e-mail sign-in through External ID.</summary>
    private readonly string emailToken =
        ExternalIdTestTokens.TokenFor("oid-email", "kris@example.com", name: "Kris E-mail");

    public IdentityLinkingTests()
    {
        factory = baseFactory.WithWebHostBuilder(builder => builder.UseExternalIdTestTokens());
    }

    public void Dispose()
    {
        factory.Dispose();
        baseFactory.Dispose();
    }

    [Fact]
    public async Task StartClaimConfirm_LinksTheNewIdentityToTheAccount()
    {
        var account = await RegisterUserAsync(factory, accountToken);

        var (linkId, code) = await StartAsync(accountToken);

        using var claimed = await ClaimAsync(emailToken, code);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var claim = await JsonAsync(claimed);
        Assert.Equal(linkId, claim.GetProperty("linkId").GetGuid());
        Assert.Equal("Kris Account", claim.GetProperty("accountDisplayName").GetString());
        Assert.Equal("ExternalId", claim.GetProperty("provider").GetString());

        using var confirmed = await ConfirmAsync(accountToken, linkId);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var identities = (await JsonAsync(confirmed)).EnumerateArray().ToArray();
        Assert.Equal(["Entra", "ExternalId"], identities.Select(identity => identity.GetProperty("provider").GetString()));
        Assert.True(identities[0].GetProperty("isCurrent").GetBoolean());
        Assert.False(identities[1].GetProperty("isCurrent").GetBoolean());

        // Signing in by e-mail now reaches the same user, without registering.
        Assert.Equal(account, await UserIdOfAsync(emailToken));
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "Users" """));
        Assert.Equal(
            "ExternalId",
            await ScalarAsync(
                factory,
                """SELECT json_extract("Details", '$.provider') FROM "AuditEvents" WHERE "Type" = 'IdentityLinked'"""));
    }

    [Fact]
    public async Task TwoMicrosoftIdentities_CanBeLinked()
    {
        // A work account and a personal account of the same person.
        var account = await RegisterUserAsync(factory, accountToken);
        var personalToken = TokenFor("oid-personal", tenantId: EntraTestTokens.PersonalAccountsTenantId);
        var (linkId, code) = await StartAsync(accountToken);

        using var claimed = await ClaimAsync(personalToken, code);
        using var confirmed = await ConfirmAsync(accountToken, linkId);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(account, await UserIdOfAsync(personalToken));

        using var list = await SendAsync(factory, HttpMethod.Get, "/api/me/identities", personalToken);
        var identities = (await JsonAsync(list)).EnumerateArray().ToArray();
        Assert.Equal([false, true], identities.Select(identity => identity.GetProperty("isPersonalMicrosoftAccount").GetBoolean()));
        Assert.Equal([false, true], identities.Select(identity => identity.GetProperty("isCurrent").GetBoolean()));
    }

    [Fact]
    public async Task NothingIsLinked_BeforeTheConfirmation()
    {
        await RegisterUserAsync(factory, accountToken);
        var (_, code) = await StartAsync(accountToken);

        using var claimed = await ClaimAsync(emailToken, code);

        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Null(await UserIdOfAsync(emailToken));
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "UserIdentities" """));
    }

    [Fact]
    public async Task SameVerifiedEmail_IsNeverLinkedWithoutTheSteps()
    {
        var verifiedFactory = baseFactory.WithWebHostBuilder(builder => builder.UseExternalIdTestTokens(emailClaimIsVerified: true));

        var account = await RegisterUserAsync(verifiedFactory, accountToken);
        var other = await RegisterUserAsync(verifiedFactory, emailToken);

        Assert.NotEqual(account, other);
    }

    [Fact]
    public async Task ConfirmBeforeClaim_IsRefused()
    {
        await RegisterUserAsync(factory, accountToken);
        var (linkId, _) = await StartAsync(accountToken);

        using var confirmed = await ConfirmAsync(accountToken, linkId);

        Assert.Equal(HttpStatusCode.Conflict, confirmed.StatusCode);
        Assert.Equal("identity_link_not_claimed", await ProblemCodeAsync(confirmed));
    }

    [Theory]
    [InlineData("unknown-code")]
    [InlineData("  ")]
    public async Task UnknownOrMissingCode_IsRefused(
        string code)
    {
        await RegisterUserAsync(factory, accountToken);
        await StartAsync(accountToken);

        using var claimed = await ClaimAsync(emailToken, code);

        Assert.Equal(HttpStatusCode.BadRequest, claimed.StatusCode);

        if (string.IsNullOrWhiteSpace(code))
        {
            Assert.True((await JsonAsync(claimed)).GetProperty("errors").TryGetProperty("code", out _));
        }
        else
        {
            Assert.Equal("identity_link_invalid", await ProblemCodeAsync(claimed));
        }
    }

    [Fact]
    public async Task ExpiredRequest_CanNeitherBeClaimedNorConfirmed()
    {
        await RegisterUserAsync(factory, accountToken);
        var (linkId, code) = await StartAsync(accountToken);
        using var claimed = await ClaimAsync(emailToken, code);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        baseFactory.Clock.UtcNow += TimeSpan.FromMinutes(10);

        using var again = await ClaimAsync(emailToken, code);
        Assert.Equal("identity_link_invalid", await ProblemCodeAsync(again));

        using var confirmed = await ConfirmAsync(accountToken, linkId);
        Assert.Equal(HttpStatusCode.BadRequest, confirmed.StatusCode);
        Assert.Equal("identity_link_invalid", await ProblemCodeAsync(confirmed));
        Assert.Null(await UserIdOfAsync(emailToken));
    }

    [Fact]
    public async Task IdentityThatIsAUserOfItsOwn_IsNeverMerged()
    {
        await RegisterUserAsync(factory, accountToken);
        var other = await RegisterUserAsync(factory, emailToken);
        var (_, code) = await StartAsync(accountToken);

        using var claimed = await ClaimAsync(emailToken, code);

        Assert.Equal(HttpStatusCode.Conflict, claimed.StatusCode);
        Assert.Equal("identity_belongs_to_another_user", await ProblemCodeAsync(claimed));
        Assert.Equal(other, await UserIdOfAsync(emailToken));
    }

    [Fact]
    public async Task IdentityRegisteredBetweenClaimAndConfirm_IsNotLinked()
    {
        await RegisterUserAsync(factory, accountToken);
        var (linkId, code) = await StartAsync(accountToken);
        using var claimed = await ClaimAsync(emailToken, code);

        // The new identity signs in normally and registers before the confirmation.
        var other = await RegisterUserAsync(factory, emailToken);
        using var confirmed = await ConfirmAsync(accountToken, linkId);

        Assert.Equal(HttpStatusCode.Conflict, confirmed.StatusCode);
        Assert.Equal("identity_belongs_to_another_user", await ProblemCodeAsync(confirmed));
        Assert.Equal(other, await UserIdOfAsync(emailToken));
        Assert.Equal(0L, await CountAsync(factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'IdentityLinked'"""));
    }

    [Fact]
    public async Task ClaimingWithTheAccountsOwnIdentity_IsAlreadyLinked()
    {
        await RegisterUserAsync(factory, accountToken);
        var (_, code) = await StartAsync(accountToken);

        using var claimed = await ClaimAsync(accountToken, code);

        Assert.Equal(HttpStatusCode.Conflict, claimed.StatusCode);
        Assert.Equal("identity_already_linked", await ProblemCodeAsync(claimed));
    }

    [Fact]
    public async Task AnotherUser_CannotConfirmOrCancelTheRequest()
    {
        await RegisterUserAsync(factory, accountToken);
        var strangerToken = TokenFor("oid-stranger");
        await RegisterUserAsync(factory, strangerToken);
        var (linkId, code) = await StartAsync(accountToken);
        using var claimed = await ClaimAsync(emailToken, code);

        using var confirmed = await ConfirmAsync(strangerToken, linkId);
        using var cancelled = await SendAsync(factory, HttpMethod.Delete, $"/api/me/identity-links/{linkId}", strangerToken);

        Assert.Equal(HttpStatusCode.NotFound, confirmed.StatusCode);
        Assert.Equal("identity_link_not_found", await ProblemCodeAsync(confirmed));
        Assert.Equal(HttpStatusCode.NotFound, cancelled.StatusCode);
        Assert.Null(await UserIdOfAsync(emailToken));

        // Still open for its owner.
        using var mine = await ConfirmAsync(accountToken, linkId);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
    }

    [Fact]
    public async Task ClaimedCode_IsUselessToAnotherIdentity_ButRepeatableForTheSameOne()
    {
        await RegisterUserAsync(factory, accountToken);
        var (_, code) = await StartAsync(accountToken);
        using var first = await ClaimAsync(emailToken, code);

        using var repeat = await ClaimAsync(emailToken, code);
        using var other = await ClaimAsync(ExternalIdTestTokens.TokenFor("oid-intruder"), code);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Equal("identity_link_invalid", await ProblemCodeAsync(other));
    }

    [Fact]
    public async Task StartingAgain_CancelsTheEarlierCode()
    {
        await RegisterUserAsync(factory, accountToken);
        var (_, oldCode) = await StartAsync(accountToken);
        var (_, newCode) = await StartAsync(accountToken);

        using var old = await ClaimAsync(emailToken, oldCode);
        using var fresh = await ClaimAsync(emailToken, newCode);

        Assert.Equal("identity_link_invalid", await ProblemCodeAsync(old));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task CancelledRequest_CanNeitherBeClaimedNorConfirmed()
    {
        await RegisterUserAsync(factory, accountToken);
        var (linkId, code) = await StartAsync(accountToken);

        using var cancelled = await SendAsync(factory, HttpMethod.Delete, $"/api/me/identity-links/{linkId}", accountToken);
        using var claimed = await ClaimAsync(emailToken, code);
        using var confirmed = await ConfirmAsync(accountToken, linkId);

        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal("identity_link_invalid", await ProblemCodeAsync(claimed));
        Assert.Equal("identity_link_invalid", await ProblemCodeAsync(confirmed));
    }

    [Fact]
    public async Task ConfirmingTwice_LinksOnce()
    {
        await RegisterUserAsync(factory, accountToken);
        var (linkId, code) = await StartAsync(accountToken);
        using var claimed = await ClaimAsync(emailToken, code);

        using var first = await ConfirmAsync(accountToken, linkId);
        using var second = await ConfirmAsync(accountToken, linkId);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await JsonAsync(second)).GetArrayLength());
        Assert.Equal(2L, await CountAsync(factory, """SELECT COUNT(*) FROM "UserIdentities" """));
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Type" = 'IdentityLinked'"""));
    }

    [Fact]
    public async Task ParallelClaimsByTwoIdentities_OnlyOneWins()
    {
        await RegisterUserAsync(factory, accountToken);
        var (_, code) = await StartAsync(accountToken);

        var responses =
            await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(index => Task.Run(() => ClaimAsync(ExternalIdTestTokens.TokenFor($"oid-racer-{index}"), code))));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.BadRequest));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task ParallelConfirmations_CreateOneIdentity()
    {
        await RegisterUserAsync(factory, accountToken);
        var (linkId, code) = await StartAsync(accountToken);
        using var claimed = await ClaimAsync(emailToken, code);

        var responses =
            await Task.WhenAll(
                Enumerable.Range(0, 6).Select(_ => Task.Run(() => ConfirmAsync(accountToken, linkId))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(2L, await CountAsync(factory, """SELECT COUNT(*) FROM "UserIdentities" """));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task OnlyTheCodesHashIsStored()
    {
        await RegisterUserAsync(factory, accountToken);
        var (_, code) = await StartAsync(accountToken);

        Assert.Equal(0L, await CountAsync(factory, """SELECT COUNT(*) FROM "IdentityLinkRequests" WHERE "CodeHash" = @p0""", code));
        Assert.Equal(1L, await CountAsync(factory, """SELECT COUNT(*) FROM "IdentityLinkRequests" WHERE length("CodeHash") = 64"""));

        // The database refuses a raw code in the hash column.
        var error =
            await Assert.ThrowsAnyAsync<Exception>(
                () => ExecuteAsync(
                    factory,
                    """UPDATE "IdentityLinkRequests" SET "CodeHash" = {0}""",
                    code));
        Assert.IsType<SqliteException>(error.GetBaseException());
    }

    [Fact]
    public async Task StartingNeedsARegisteredActiveUser()
    {
        using var unregistered = await SendAsync(factory, HttpMethod.Post, "/api/me/identity-links", accountToken);
        Assert.Equal(HttpStatusCode.Forbidden, unregistered.StatusCode);
        Assert.Equal("user_not_registered", await ProblemCodeAsync(unregistered));

        await RegisterUserAsync(factory, accountToken);
        await ExecuteAsync(factory, """UPDATE "Users" SET "DisabledUtc" = 1""");

        using var disabled = await SendAsync(factory, HttpMethod.Post, "/api/me/identity-links", accountToken);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("user_disabled", await ProblemCodeAsync(disabled));
    }

    [Fact]
    public async Task DisabledAccount_CannotReceiveANewIdentity()
    {
        await RegisterUserAsync(factory, accountToken);
        var (_, code) = await StartAsync(accountToken);
        await ExecuteAsync(factory, """UPDATE "Users" SET "DisabledUtc" = 1""");

        using var claimed = await ClaimAsync(emailToken, code);

        Assert.Equal("identity_link_invalid", await ProblemCodeAsync(claimed));
    }

    [Fact]
    public async Task WithoutATokenEveryStepIsUnauthorized()
    {
        using var start = await SendAsync(factory, HttpMethod.Post, "/api/me/identity-links", null);
        using var claim = await SendAsync(factory, HttpMethod.Post, "/api/identity-links/claim", null, new { code = "x" });
        using var list = await SendAsync(factory, HttpMethod.Get, "/api/me/identities", null);

        Assert.All([start, claim, list], response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task WithoutSignInConfigured_TheEndpointsAnswer503()
    {
        using var plain = new MeetingRoomBookingApiFactory();

        using var response = await SendAsync(plain, HttpMethod.Get, "/api/me/identities", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("authentication_not_configured", await ProblemCodeAsync(response));
    }

    // Helpers

    private async Task<(Guid LinkId, string Code)> StartAsync(
        string token)
    {
        using var response = await SendAsync(factory, HttpMethod.Post, "/api/me/identity-links", token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await JsonAsync(response);
        return (body.GetProperty("linkId").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private Task<HttpResponseMessage> ClaimAsync(
        string token,
        string code) =>
        SendAsync(factory, HttpMethod.Post, "/api/identity-links/claim", token, new { code });

    private Task<HttpResponseMessage> ConfirmAsync(
        string token,
        Guid linkId) =>
        SendAsync(factory, HttpMethod.Post, $"/api/me/identity-links/{linkId}/confirm", token);

    /// <summary>The user the identity signs in as, or null when it is not registered.</summary>
    private async Task<Guid?> UserIdOfAsync(
        string token)
    {
        using var response = await GetMeAsync(factory, token);
        var user = (await JsonAsync(response)).GetProperty("user");
        return user.ValueKind == JsonValueKind.Null ? null : user.GetProperty("publicId").GetGuid();
    }
}
