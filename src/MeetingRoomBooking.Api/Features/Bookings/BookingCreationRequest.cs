using System.Security.Cryptography;
using System.Text.Json;

namespace MeetingRoomBooking.Api.Features.Bookings;

/// <summary>
/// A booking creation that carried an Idempotency-Key, stored in the same
/// transaction as the booking it created. A repeated request with the same
/// key, by the same caller in the same organisation, gets that booking
/// instead of creating another.
/// </summary>
/// <remarks>
/// Only successful creations are stored: a refused request (overlap,
/// unknown room, start in the past, lost access) writes nothing, so
/// repeating it simply runs it again. Every keyed request deletes the rows
/// older than <see cref="RetentionPeriod"/> inside its transaction before
/// its lookup, so it never answers from an expired key; the deletion is
/// kept when that request commits, i.e. by the next successful keyed
/// creation.
/// </remarks>
public sealed class BookingCreationRequest
{
    /// <summary>
    /// How long a key is remembered. A client repeats a request whose answer
    /// it lost within seconds or minutes; a day leaves ample room for a
    /// phone that was offline for a while.
    /// </summary>
    public static readonly TimeSpan RetentionPeriod =
        TimeSpan.FromHours(24);

    /// <summary>Length of a hex-encoded SHA-256 hash.</summary>
    internal const int RequestHashLength = 64;

    private BookingCreationRequest()
    {
    }

    public long Id { get; private set; }

    public int OrganizationId { get; private set; }

    /// <summary>
    /// The signed-in caller who sent the key. Null only for the legacy
    /// endpoints in anonymous demo mode, where every anonymous caller shares
    /// one scope (they can already read every legacy booking).
    /// </summary>
    public int? UserId { get; private set; }

    public Guid IdempotencyKey { get; private set; }

    /// <summary>
    /// Hash of the request the key was first used with (see
    /// <see cref="HashOf"/>), so the same key with another request is refused.
    /// </summary>
    public string RequestHash { get; private set; } =
        string.Empty;

    /// <summary>The booking it created; null once that booking has been deleted.</summary>
    public int? BookingId { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public static BookingCreationRequest Record(
        int organizationId,
        int? userId,
        Guid idempotencyKey,
        string requestHash,
        int bookingId,
        DateTimeOffset createdUtc)
    {
        if (requestHash.Length != RequestHashLength)
        {
            throw new ArgumentException(
                "Expected a hex-encoded SHA-256 hash.",
                nameof(requestHash));
        }

        return new BookingCreationRequest
        {
            OrganizationId = organizationId,
            UserId = userId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            BookingId = bookingId,
            CreatedUtc = createdUtc.ToUniversalTime()
        };
    }

    /// <summary>
    /// Lower-case hex SHA-256 of the request as the client sent it, before
    /// the API fills in defaults (such as "booked by" from the caller's
    /// name), so a changed display name does not turn a repeat into another
    /// request. Times are compared as instants: the same moment with another
    /// UTC offset is the same request.
    /// </summary>
    public static string HashOf(
        SaveBookingRequest request)
    {
        var canonical =
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    request.RoomId,
                    request.Title,
                    request.BookedBy,
                    StartUtcTicks = request.StartUtc.UtcTicks,
                    EndUtcTicks = request.EndUtc.UtcTicks
                });

        return Convert.ToHexStringLower(
            SHA256.HashData(canonical));
    }
}

/// <summary>An Idempotency-Key from a booking request, with the hash of the request it came with.</summary>
public sealed record IdempotentBookingRequest(
    Guid Key,
    string RequestHash)
{
    public const string InvalidKeyMessage =
        "Send a new random GUID in the " + IdempotencyHeaders.Key +
        " header with every new booking, and the same one when repeating a request.";

    /// <summary>
    /// Reads the header. No header: the request is not idempotent, as
    /// before keys existed (<paramref name="result"/> is null). A header that
    /// is not a non-empty GUID: false.
    /// </summary>
    public static bool TryRead(
        string? header,
        SaveBookingRequest request,
        out IdempotentBookingRequest? result)
    {
        result = null;

        if (header is null)
        {
            return true;
        }

        if (!Guid.TryParse(header, out var key) ||
            key == Guid.Empty)
        {
            return false;
        }

        result = new IdempotentBookingRequest(
            key,
            BookingCreationRequest.HashOf(request));

        return true;
    }
}
