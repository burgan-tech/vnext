using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Dispatches a state notification through the platform-managed <c>state</c> channel
/// (Dapr binding <c>vnext-notification-state</c>). Shared by the <c>NotificationTask</c> state
/// channel and the state-level <c>notification</c> directive so both produce an identical
/// slim payload and use the same invoke path.
/// </summary>
public interface IStateNotificationDispatcher
{
    /// <summary>
    /// Compiles the optional State Notify Mapping, builds the slim state message and invokes the
    /// state Dapr binding.
    /// </summary>
    /// <param name="scriptContext">Script context carrying the workflow and settled instance.</param>
    /// <param name="mapping">
    /// Optional State Notify Mapping (compiled to an <c>IStateNotificationMapping</c>).
    /// Pass <c>null</c> or an empty mapping for default metadata only.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> DispatchAsync(
        ScriptContext scriptContext,
        ScriptCode? mapping,
        CancellationToken cancellationToken);
}
