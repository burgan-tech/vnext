namespace BBT.Workflow.Runtime;

/// <summary>
/// The domain the current logical operation (an HTTP request, a job, an inbox event, a relayed
/// command) is serving, when one process hosts several domains.
/// </summary>
/// <remarks>
/// An <see cref="AsyncLocal{T}"/> value, the same shape as Aether's current-schema scope: every entry
/// point opens a scope and disposes it when the operation ends, and everything awaited inside it reads
/// the same domain through <see cref="IRuntimeInfoProvider.Domain"/>. That getter honours the scope
/// only for a hosted domain, so a scope opened for a foreign domain is inert; outside any scope it
/// falls back to the primary hosted domain, which is exactly single-domain behaviour.
/// <para>
/// HTTP requests open the scope through <see cref="IRuntimeInfoProvider.UseDomain"/>, which rejects a
/// domain that is not hosted. Jobs, inbox events and relayed commands carry a domain the runtime wrote
/// itself and open it with <see cref="Begin"/>.
/// </para>
/// </remarks>
public static class DomainScope
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    /// <summary>The domain of the current scope, or <c>null</c> outside any scope.</summary>
    public static string? Current => CurrentValue.Value;

    /// <summary>
    /// Opens a scope for <paramref name="domain"/>; disposing it restores the enclosing value.
    /// A blank domain opens no scope.
    /// </summary>
    public static IDisposable Begin(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return NoScope.Instance;
        }

        var previous = CurrentValue.Value;
        CurrentValue.Value = domain;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentValue.Value = previous;
        }
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
