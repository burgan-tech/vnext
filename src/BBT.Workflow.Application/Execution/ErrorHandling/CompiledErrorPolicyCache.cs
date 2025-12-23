using System.Collections.Concurrent;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Cache for compiled error policies per workflow.
/// Stores pre-compiled error boundaries to avoid repeated sorting and matching overhead.
/// </summary>
public sealed class CompiledErrorPolicyCache : ICompiledErrorPolicyCache
{
    private readonly ConcurrentDictionary<string, WorkflowCompiledPolicies> _cache = new();

    /// <inheritdoc />
    public WorkflowCompiledPolicies GetOrCompile(Definitions.Workflow workflow)
    {
        var cacheKey = GetCacheKey(workflow);

        return _cache.GetOrAdd(cacheKey, _ => CompileWorkflowPolicies(workflow));
    }

    /// <inheritdoc />
    public void Invalidate(string domain, string workflowKey, string version)
    {
        var cacheKey = $"{domain}:{workflowKey}:{version}";
        _cache.TryRemove(cacheKey, out _);
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Gets the cache key for a workflow.
    /// </summary>
    private static string GetCacheKey(Definitions.Workflow workflow)
    {
        return $"{workflow.Domain}:{workflow.Key}:{workflow.Version}";
    }

    /// <summary>
    /// Compiles all error policies for a workflow.
    /// </summary>
    private static WorkflowCompiledPolicies CompileWorkflowPolicies(Definitions.Workflow workflow)
    {
        var policies = new WorkflowCompiledPolicies(workflow.Domain, workflow.Key, workflow.Version);

        // Compile global boundary
        if (workflow.ErrorBoundary != null)
        {
            policies.GlobalBoundary = CompiledBoundary.Compile(
                workflow.ErrorBoundary,
                ErrorBoundaryLevel.Global,
                "global");
        }

        // Compile state boundaries
        foreach (var state in workflow.States)
        {
            if (state.ErrorBoundary != null)
            {
                policies.StateBoundaries[state.Key] = CompiledBoundary.Compile(
                    state.ErrorBoundary,
                    ErrorBoundaryLevel.State,
                    state.Key);
            }
        }

        return policies;
    }
}

/// <summary>
/// Interface for the compiled error policy cache.
/// </summary>
public interface ICompiledErrorPolicyCache
{
    /// <summary>
    /// Gets or compiles error policies for a workflow.
    /// </summary>
    /// <param name="workflow">The workflow to get/compile policies for.</param>
    /// <returns>The compiled policies for the workflow.</returns>
    WorkflowCompiledPolicies GetOrCompile(Definitions.Workflow workflow);

    /// <summary>
    /// Invalidates cached policies for a specific workflow version.
    /// </summary>
    void Invalidate(string domain, string workflowKey, string version);

    /// <summary>
    /// Invalidates all cached policies.
    /// </summary>
    void InvalidateAll();
}

/// <summary>
/// Contains all compiled error policies for a workflow.
/// </summary>
public sealed class WorkflowCompiledPolicies
{
    /// <summary>
    /// The workflow domain.
    /// </summary>
    public string Domain { get; }

    /// <summary>
    /// The workflow key.
    /// </summary>
    public string WorkflowKey { get; }

    /// <summary>
    /// The workflow version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// The compiled global error boundary.
    /// </summary>
    public CompiledBoundary? GlobalBoundary { get; set; }

    /// <summary>
    /// Compiled state-level error boundaries, keyed by state key.
    /// </summary>
    public Dictionary<string, CompiledBoundary> StateBoundaries { get; } = new();

    /// <summary>
    /// Compiled task-level error boundaries, keyed by task key.
    /// Note: Task boundaries are typically compiled on-demand since they're task-specific.
    /// </summary>
    public Dictionary<string, CompiledBoundary> TaskBoundaries { get; } = new();

    /// <summary>
    /// Initializes a new instance of WorkflowCompiledPolicies.
    /// </summary>
    public WorkflowCompiledPolicies(string domain, string workflowKey, string version)
    {
        Domain = domain;
        WorkflowKey = workflowKey;
        Version = version;
    }

    /// <summary>
    /// Gets the compiled boundary for a state, or null if not configured.
    /// </summary>
    public CompiledBoundary? GetStateBoundary(string stateKey)
    {
        return StateBoundaries.GetValueOrDefault(stateKey);
    }

    /// <summary>
    /// Gets or compiles a task boundary.
    /// Task boundaries are compiled on-demand and cached.
    /// </summary>
    /// <param name="taskKey">The task reference key.</param>
    /// <param name="errorBoundary">The error boundary from the OnExecuteTask configuration.</param>
    /// <returns>The compiled boundary, or null if no boundary configured.</returns>
    public CompiledBoundary? GetOrCompileTaskBoundary(string taskKey, ErrorBoundary? errorBoundary)
    {
        if (errorBoundary == null)
            return null;

        // Use a composite key since same task can have different error boundaries in different contexts
        var cacheKey = $"{taskKey}:{errorBoundary.GetHashCode()}";

        if (TaskBoundaries.TryGetValue(cacheKey, out var cached))
            return cached;

        var compiled = CompiledBoundary.Compile(
            errorBoundary,
            ErrorBoundaryLevel.Task,
            taskKey);

        TaskBoundaries[cacheKey] = compiled;
        return compiled;
    }
}

