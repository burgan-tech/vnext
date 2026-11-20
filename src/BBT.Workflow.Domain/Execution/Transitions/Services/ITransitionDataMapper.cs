using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Execution.Transitions.Services;

/// <summary>
/// Service responsible for mapping transition payload data to instance data.
/// Handles optional transition mapping scripts to transform payload before adding to instance.
/// </summary>
public interface ITransitionDataMapper
{
    /// <summary>
    /// Maps transition payload data to JsonData, applying transition mapping script if available.
    /// If no mapping is defined, returns the payload as-is converted to JsonData.
    /// </summary>
    /// <param name="payload">The original payload data from the transition request</param>
    /// <param name="transition">The transition definition (may be null for start transitions)</param>
    /// <param name="workflow">The workflow definition</param>
    /// <param name="instance">The workflow instance</param>
    /// <param name="runtimeInfoProvider">Runtime information provider for script context</param>
    /// <param name="headers">Request headers for script context</param>
    /// <param name="onScriptFailure">
    /// Receives the exception when the mapping script throws, for the incident's stack trace or the
    /// log (vnext#1268); the returned error carries only its message.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the mapped JsonData or an error if mapping execution fails</returns>
    Task<Result<object?>> MapTransitionDataAsync(
        object? payload,
        Transition? transition,
        Definitions.Workflow workflow,
        Instance instance,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers = null,
        Action<Exception>? onScriptFailure = null,
        CancellationToken cancellationToken = default);
}
