using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeetingRoomBooking.Api.IntegrationTests;

/// <summary>Where <see cref="TransactionGate"/> holds a request.</summary>
public enum TransactionGatePoint
{
    /// <summary>After the access filter, before <c>BEGIN IMMEDIATE</c>: no lock is held yet.</summary>
    BeforeBegin,

    /// <summary>Right after <c>BEGIN IMMEDIATE</c>: the request holds the write lock.</summary>
    AfterBegin,

    /// <summary>Right after <c>COMMIT</c>: the change is durable, the response not yet sent.</summary>
    AfterCommit
}

/// <summary>
/// Test-only seam: holds the next database transaction the API starts at a
/// chosen point until the test releases it, so a test can change a
/// membership at an exact moment of a request instead of racing it.
/// </summary>
/// <remarks>
/// An EF Core interceptor that only the test host registers
/// (<see cref="Organizations.TenantScenario"/>); the API has no such hook.
/// Idle unless a test calls <see cref="PauseNext"/>, and then it holds one
/// transaction only.
/// </remarks>
public sealed class TransactionGate
    : DbTransactionInterceptor
{
    private PausedTransaction? armed;

    public PausedTransaction PauseNext(
        TransactionGatePoint point)
    {
        var paused = new PausedTransaction(point);

        if (Interlocked.CompareExchange(ref armed, paused, null) is not null)
        {
            throw new InvalidOperationException("Another transaction is already being held.");
        }

        return paused;
    }

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync(TransactionGatePoint.BeforeBegin);
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync(TransactionGatePoint.AfterBegin);
        return result;
    }

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync(TransactionGatePoint.AfterCommit);
    }

    // Only the first transaction to reach the armed point is held.
    private Task HoldAsync(
        TransactionGatePoint point)
    {
        var paused = Volatile.Read(ref armed);

        return paused is not null &&
               paused.Point == point &&
               Interlocked.CompareExchange(ref armed, null, paused) == paused
            ? paused.HoldAsync()
            : Task.CompletedTask;
    }
}

/// <summary>A transaction held by <see cref="TransactionGate"/>. Disposing it releases it.</summary>
public sealed class PausedTransaction(
    TransactionGatePoint point)
    : IDisposable
{
    private readonly TaskCompletionSource reached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TransactionGatePoint Point { get; } = point;

    /// <summary>Completes when a request's transaction is being held.</summary>
    public Task Reached =>
        reached.Task;

    public void Release() =>
        released.TrySetResult();

    public void Dispose() =>
        Release();

    internal Task HoldAsync()
    {
        reached.TrySetResult();
        return released.Task;
    }
}
