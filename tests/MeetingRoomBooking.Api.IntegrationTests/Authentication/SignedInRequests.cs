using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeetingRoomBooking.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static MeetingRoomBooking.Api.IntegrationTests.Authentication.EntraTestTokens;

namespace MeetingRoomBooking.Api.IntegrationTests.Authentication;

/// <summary>Requests with a test access token, and direct reads of the test database.</summary>
public static class SignedInRequests
{
    /// <summary>A delegated token for one person: their own object ID, optionally a verified e-mail.</summary>
    public static string TokenFor(
        string objectId,
        string? email = null,
        bool emailVerified = true,
        string tenantId = TenantId,
        string name = "Søren Ærø")
    {
        var claims = UserClaims(tenantId);
        claims["oid"] = objectId;
        claims["name"] = name;

        if (email is not null)
        {
            claims["email"] = email;

            if (emailVerified)
            {
                claims["xms_edov"] = true;
            }
        }

        return Create(claims);
    }

    public static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> factory,
        HttpMethod method,
        string path,
        string? token,
        object? body = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);

        if (token is not null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> RegisterAsync(
        WebApplicationFactory<Program> factory,
        string? token) =>
        SendAsync(factory, HttpMethod.Post, "/api/me/registration", token);

    public static Task<HttpResponseMessage> GetMeAsync(
        WebApplicationFactory<Program> factory,
        string? token) =>
        SendAsync(factory, HttpMethod.Get, "/api/me", token);

    /// <summary>Registers and returns the new user's public ID.</summary>
    public static async Task<Guid> RegisterUserAsync(
        WebApplicationFactory<Program> factory,
        string token)
    {
        using var response = await RegisterAsync(factory, token);
        response.EnsureSuccessStatusCode();

        var body = await JsonAsync(response);
        return body.GetProperty("currentUser").GetProperty("user").GetProperty("publicId").GetGuid();
    }

    public static async Task<JsonElement> JsonAsync(
        HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<string?> ProblemCodeAsync(
        HttpResponseMessage response)
    {
        var body = await JsonAsync(response);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task<long> CountAsync(
        WebApplicationFactory<Program> factory,
        string sql,
        params object[] parameters)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<BookingDbContext>().Database;
        var connection = database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@p{index}";
                parameter.Value = parameters[index];
                command.Parameters.Add(parameter);
            }

            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    public static async Task ExecuteAsync(
        WebApplicationFactory<Program> factory,
        string sql,
        params object[] parameters)
    {
        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider
            .GetRequiredService<BookingDbContext>()
            .Database
            .ExecuteSqlRawAsync(sql, parameters);
    }
}
