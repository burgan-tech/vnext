using BBT.Aether.DistributedLock;
using BBT.Workflow.Logging;

namespace BBT.Workflow.HttpApi.Shared.Telemetry;

/// <summary>
/// Pass-through <see cref="IDistributedLockService"/> decorator that publishes the lock resource
/// id into the <see cref="DaprCallLabel"/> ambient for the duration of each call, so
/// <see cref="DaprSpanLabelProcessor"/> can stamp it onto the Dapr gRPC client span the call
/// produces (TryLockAlpha1/UnlockAlpha1). No behavior change — labelling only.
/// <para>
/// Note the handle returned by <see cref="TryAcquireLockAsync"/> releases the lock on dispose,
/// OUTSIDE this scope — that Unlock span gets no label. The status-lock release goes through
/// <see cref="ReleaseLockAsync"/>-style explicit paths or the handle; where the handle is used,
/// the acquire span still identifies the resource, which is what lock-contention debugging needs.
/// </para>
/// </summary>
public sealed class LabellingDistributedLockService(IDistributedLockService inner) : IDistributedLockService
{
    public async Task<IDistributedLockHandle?> TryAcquireLockAsync(
        string resourceId,
        int expiryInSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        using var scope = DaprCallLabel.Use(resourceId);
        return await inner.TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);
    }

    public async Task<bool> ReleaseLockAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        using var scope = DaprCallLabel.Use(resourceId);
        return await inner.ReleaseLockAsync(resourceId, cancellationToken);
    }

    public async Task<(bool Acquired, T? Result)> ExecuteWithLockAsync<T>(
        string resourceId,
        Func<Task<T>> function,
        int expiryInSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        // The scope spans the body too; body-internal cache calls re-label via their own Use and
        // unwind back to this resource id for the trailing Unlock.
        using var scope = DaprCallLabel.Use(resourceId);
        return await inner.ExecuteWithLockAsync(resourceId, function, expiryInSeconds, cancellationToken);
    }

    public async Task<bool> ExecuteWithLockAsync(
        string resourceId,
        Func<Task> action,
        int expiryInSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        using var scope = DaprCallLabel.Use(resourceId);
        return await inner.ExecuteWithLockAsync(resourceId, action, expiryInSeconds, cancellationToken);
    }
}
