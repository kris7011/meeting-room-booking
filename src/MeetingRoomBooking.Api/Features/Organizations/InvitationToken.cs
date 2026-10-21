using System.Security.Cryptography;
using System.Text;

namespace MeetingRoomBooking.Api.Features.Organizations;

/// <summary>
/// Invitation tokens: 32 random bytes, URL-safe, sent only in the invitation
/// link. The database keeps nothing but their SHA-256 hash, so a copy of the
/// database cannot be used to accept an invitation.
/// </summary>
public static class InvitationToken
{
    /// <summary>Length of the lower-case hex SHA-256 hash.</summary>
    public const int HashLength = 64;

    private const int TokenBytes = 32;

    /// <returns>The token for the link, and the hash to store.</returns>
    public static (string Token, string Hash) Create()
    {
        var token =
            Base64UrlEncode(
                RandomNumberGenerator.GetBytes(TokenBytes));

        return (token, Hash(token));
    }

    public static string Hash(
        string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        return Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(token)));
    }

    private static string Base64UrlEncode(
        byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
