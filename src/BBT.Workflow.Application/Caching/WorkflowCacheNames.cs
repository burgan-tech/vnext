namespace BBT.Workflow.Caching;

/// <summary>
/// Names of FusionCache instances registered for the workflow runtime.
/// Centralized here so consumers and DI registration can't drift.
/// </summary>
public static class WorkflowCacheNames
{
    /// <summary>
    /// Short-TTL local cache for <see cref="IComponentVersionIndex"/> lookups.
    /// Memory-only; protects Redis from "latest version" stampede during execution bursts.
    /// </summary>
    public const string VersionIndex = "workflow.vidx";
}
