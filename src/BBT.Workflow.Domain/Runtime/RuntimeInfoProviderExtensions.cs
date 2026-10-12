namespace BBT.Workflow.Runtime;

/// <summary>Helpers over <see cref="IRuntimeInfoProvider"/>.</summary>
public static class RuntimeInfoProviderExtensions
{
    /// <summary>
    /// The domain to write into a background job's name: <paramref name="domain"/> when this process
    /// hosts several domains, otherwise <c>null</c> — a single-domain process keeps the
    /// <c>vnext.job.v1</c> names it always had. <c>null</c> provider (code built by hand, tests) → <c>null</c>.
    /// </summary>
    public static string? JobDomain(this IRuntimeInfoProvider? runtimeInfoProvider, string? domain) =>
        runtimeInfoProvider?.HostedDomains is { Count: > 1 } && !string.IsNullOrWhiteSpace(domain)
            ? domain
            : null;
}
