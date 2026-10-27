namespace MeetingRoomBooking.Api.Features;

/// <summary>
/// The headers of the API's idempotent creation endpoints (organisations and
/// bookings), after the IETF "Idempotency-Key HTTP Header Field" draft.
/// </summary>
public static class IdempotencyHeaders
{
    /// <summary>A client-chosen random GUID per operation, the same one when the request is repeated.</summary>
    public const string Key = "Idempotency-Key";

    /// <summary>"true" on a response that repeats the result of an earlier request with the same key.</summary>
    public const string Replayed = "Idempotent-Replayed";
}
