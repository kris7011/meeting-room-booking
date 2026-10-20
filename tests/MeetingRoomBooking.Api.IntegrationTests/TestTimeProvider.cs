namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>
/// A clock that only moves when a test sets it, so time-dependent rules can
/// be tested at exact instants.
/// </summary>
public sealed class TestTimeProvider(
    DateTimeOffset utcNow)
    : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } =
        utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        return UtcNow;
    }
}
