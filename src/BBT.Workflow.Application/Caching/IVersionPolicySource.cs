using BBT.Aether.Results;

namespace BBT.Workflow.Caching;

/// <summary>
/// Supplies a component's default-version pointer to <see cref="CacheSet{T}"/>'s floating resolution
/// (<c>latest</c>, MAJOR, MAJOR.MINOR requests).
/// </summary>
/// <remarks>
/// A null pointer means the component has no default and resolves automatically, exactly as before the
/// default-version policy existed. A failed read fails the resolution: it never falls back to
/// <c>latest</c>, because that would silently activate a version the owner has not activated.
/// </remarks>
public interface IVersionPolicySource
{
    /// <summary>
    /// Reads the default-version pointer of <paramref name="key"/> in <paramref name="domain"/>.
    /// </summary>
    /// <param name="domain">The component's domain.</param>
    /// <param name="key">The component key (for a workflow, the flow key).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The pointer, or null for automatic resolution; a failure when the source cannot be read.</returns>
    Task<Result<string?>> GetDefaultVersionAsync(string domain, string key, CancellationToken ct);
}

/// <summary>
/// The policy source for component sets that have no default-version policy: every component resolves
/// automatically. <see cref="CacheSet{T}"/> recognises it and skips the policy step altogether, so a set
/// wired with it behaves exactly as it did before the policy existed.
/// </summary>
public sealed class NoVersionPolicySource : IVersionPolicySource
{
    /// <summary>The shared instance.</summary>
    public static readonly NoVersionPolicySource Instance = new();

    private NoVersionPolicySource()
    {
    }

    /// <inheritdoc />
    public Task<Result<string?>> GetDefaultVersionAsync(string domain, string key, CancellationToken ct)
        => Task.FromResult(Result<string?>.Ok(null));
}
