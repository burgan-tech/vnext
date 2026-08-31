namespace BBT.Workflow.Instances;

/// <summary>
/// The process-wide, striped per-instance write gate. Every in-process writer that
/// read-modify-writes state belonging to ONE instance takes this gate first, so that two
/// branches of the same instance never touch the shared DbContext concurrently.
/// <para>
/// Why it exists: parallel task branches (fan-out) each get their own DI scope, but Aether's
/// ambient (AsyncLocal) UnitOfWork flows into every branch and hands them all the SAME
/// schema-bound <c>WorkflowDbContext</c>. A Npgsql connection cannot run two commands at once,
/// so concurrent writers surface as
/// <c>"A second operation was started on this context instance before a previous operation
/// completed"</c> (NpgsqlOperationInProgressException). The gate is taken BEFORE the context is
/// even resolved, because resolving it can itself touch the shared connection
/// (context/schema materialization inside the ambient UnitOfWork).
/// </para>
/// <para>
/// Why it is SHARED and must stay shared: the writers are not independent. A fan-out item writing
/// a SubProcess correlation onto the parent aggregate and the parent's own instance-data append
/// genuinely overlap. If each writer kept its OWN striped array, two writers for the same instance
/// would take two DIFFERENT semaphores, serialize against nobody, and collide on the shared
/// DbContext exactly as before. <b>Splitting this back into per-writer gate arrays reintroduces
/// the bug.</b> One gate, one array, every writer.
/// </para>
/// <para>
/// Striping bounds memory: <see cref="StripeCount"/> semaphores, selected by instance id hash.
/// A hash collision merely serializes two unrelated instances in-process — harmless, and the
/// database-level per-instance row lock would have serialized them across processes anyway.
/// The selection function is part of the contract: two writers MUST land on the same stripe for
/// the same instance, so do not change it independently in one call site.
/// </para>
/// <para>
/// The gate is a plain non-reentrant <see cref="SemaphoreSlim"/>. Never call a gated writer from
/// inside a held gate for the same instance — hold it only around the read-modify-write itself,
/// never across task execution, script evaluation or an outbound call.
/// </para>
/// </summary>
public static class InstanceWriteGate
{
    /// <summary>
    /// Number of stripes. Bounded so the gate costs a fixed amount of memory regardless of how
    /// many instances the process has seen. Sized so that unrelated instances rarely share a
    /// stripe under realistic concurrency — the gate is held across a multi-round-trip database
    /// write, so a collision serializes real latency, and a semaphore costs well under 100 bytes.
    /// </summary>
    private const int StripeCount = 256;

    private static readonly SemaphoreSlim[] Gates =
        Enumerable.Range(0, StripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>
    /// The stripe an instance id maps to. Exposed for diagnostics and for tests that need two ids
    /// known to land on different stripes; the mapping itself is an implementation detail that all
    /// callers share by going through <see cref="AcquireAsync"/>.
    /// </summary>
    public static int StripeIndexOf(Guid instanceId)
        => (instanceId.GetHashCode() & int.MaxValue) % StripeCount;

    internal static SemaphoreSlim GateFor(Guid instanceId) => Gates[StripeIndexOf(instanceId)];

    /// <summary>
    /// Acquires the gate for <paramref name="instanceId"/>. Dispose the returned handle to
    /// release it — intended to be used as <c>using var _ = await
    /// InstanceWriteGate.AcquireAsync(id, ct);</c> around the whole read-modify-write.
    /// </summary>
    public static async Task<Releaser> AcquireAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var gate = GateFor(instanceId);
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    /// <summary>
    /// Releases the per-instance gate exactly once when disposed.
    /// </summary>
    public readonly struct Releaser(SemaphoreSlim gate) : IDisposable
    {
        /// <summary>
        /// Releases the gate acquired by <see cref="AcquireAsync"/>.
        /// </summary>
        public void Dispose() => gate?.Release();
    }
}
