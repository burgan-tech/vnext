using System.Collections.Immutable;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Tracks the transition lock keys held by the current logical execution chain.
/// Enables chain-reentrant lock acquisition: when a nested operation (e.g. a sync subflow
/// completion callback running inside the parent's post-commit phase) needs a lock key that
/// is already held higher up in the same async call chain, it can proceed without
/// re-acquiring — mutual exclusion is already provided by the outer holder.
/// Registrations are AsyncLocal-scoped: they flow into awaited child calls and
/// automatically disappear when the registering async method returns.
/// </summary>
public static class ChainLockRegistry
{
    private static readonly AsyncLocal<ImmutableHashSet<string>?> HeldKeys = new();

    /// <summary>
    /// Registers a lock key as held by the current execution chain.
    /// Call only after the distributed lock has actually been acquired, from the method
    /// whose lexical scope owns the lock (the registration is visible to awaited callees
    /// and expires when that method returns).
    /// </summary>
    /// <param name="lockKey">The acquired lock key.</param>
    public static void Register(string lockKey)
    {
        HeldKeys.Value = (HeldKeys.Value ?? ImmutableHashSet.Create<string>(StringComparer.Ordinal))
            .Add(lockKey);
    }

    /// <summary>
    /// Returns <c>true</c> when the given lock key is already held by the current execution chain.
    /// </summary>
    /// <param name="lockKey">The lock key to check.</param>
    public static bool IsHeld(string lockKey) => HeldKeys.Value?.Contains(lockKey) == true;
}
