using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution.Transitions.Services;

/// <summary>
/// Service responsible for mapping transition payload data to instance data.
/// Handles an optional transition mapping script, or a declarative <c>output</c> block, that transforms the payload
/// before it is added to the instance.
/// </summary>
public interface ITransitionDataMapper
{
    /// <summary>
    /// Maps transition payload data, applying the transition's mapping script or, without one, its declarative
    /// <c>output</c> block (a stored transition with both runs the script only). If neither is defined, returns the
    /// payload as-is. A block returns only what it wrote (null when it wrote nothing).
    /// </summary>
    /// <param name="payload">The original payload data from the transition request</param>
    /// <param name="transition">The transition definition (may be null for start transitions)</param>
    /// <param name="workflow">The workflow definition</param>
    /// <param name="instance">The workflow instance</param>
    /// <param name="headers">Request headers for script context</param>
    /// <param name="onScriptFailure">
    /// Receives the exception when the mapping script throws, for the incident's stack trace or the
    /// log (vnext#1268); the returned error carries only its message. A failing declarative block has no exception: it
    /// is not called for one (the mapper logs <c>TransitionDeclarativeOutputFailed</c> instead).
    /// </param>
    /// <param name="actor">
    /// Who drove the hop; a declarative block's <c>user</c> is that caller (<c>ICallerAccessorFactory.ForHop</c>).
    /// Null (or no caller factory) means no caller: <c>user.available</c> is false and a role read fails.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the mapped JsonData or an error if mapping execution fails</returns>
    Task<Result<object?>> MapTransitionDataAsync(
        object? payload,
        Transition? transition,
        Definitions.Workflow workflow,
        Instance instance,
        IReadOnlyDictionary<string, string?>? headers = null,
        Action<Exception>? onScriptFailure = null,
        ExecutionActor? actor = null,
        CancellationToken cancellationToken = default);
}
