using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Instances;

/// <summary>
/// Validates workflow master data and commits instance-data mutations only after validation succeeds.
/// </summary>
public interface IInstanceDataMutationService
{
    /// <summary>
    /// Merges <paramref name="inputData"/> with the current instance data, validates the complete
    /// candidate against the workflow master schema, and commits the mutation on success.
    /// </summary>
    Task<Result<InstanceData>> AddDataAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Guid id,
        JsonData inputData,
        VersionStrategy? versionStrategy = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? headers = null);

    /// <summary>
    /// Validates the complete explicit-version payload against the workflow master schema and
    /// commits the mutation on success.
    /// </summary>
    Task<Result<InstanceData>> AddDataWithVersionAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Guid id,
        JsonData inputData,
        string version,
        bool ignoreSameData = true,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? headers = null);

    /// <summary>
    /// Validates every new full-snapshot data version produced inside a script context, then
    /// atomically imports those versions and non-data script mutations into the live transition
    /// aggregate. No live mutation is applied when any candidate fails validation.
    /// </summary>
    Task<Result> ApplyScriptContextChangesAsync(
        Definitions.Workflow workflow,
        TransitionExecutionContext transitionContext,
        ScriptContext scriptContext,
        CancellationToken cancellationToken = default);
}
