using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// HTTP task executed directly by the Orchestrator process (type discriminator "21").
/// The orchestrator performs the HTTP call in-process instead of routing the invocation
/// through the Execution service's <c>/execution/invoke/{type}/{key}</c> hop.
/// </summary>
/// <remarks>
/// Derives from <see cref="HttpTask"/> so the configuration surface (<c>url</c>, <c>method</c>,
/// <c>headers</c>, <c>body</c>, <c>contentType</c>, <c>rawBody</c>, <c>timeoutSeconds</c>,
/// <c>validateSsl</c>, <c>acceptedStatusCodes</c>) and the scripting surface are identical —
/// mapping scripts that cast <c>task as HttpTask</c> and call <c>SetUrl</c>/<c>SetHeaders</c>/
/// <c>SetBody</c> work unchanged. Only the execution location differs; see
/// <c>LocalHttpTaskExecutor</c>.
/// </remarks>
public sealed class LocalHttpTask : HttpTask
{
    private LocalHttpTask()
    {
    }

    [JsonConstructor]
    private LocalHttpTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.LocalHttp).ToString();
    }

    public static LocalHttpTask Create(
        JsonElement config)
    {
        return new LocalHttpTask(config);
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static new LocalHttpTask CreateEmpty()
    {
        return new LocalHttpTask();
    }

    /// <summary>
    /// Creates a deep copy of the current LocalHttpTask instance. The base
    /// <see cref="HttpTask.CloneTyped"/> would materialize a plain <see cref="HttpTask"/>,
    /// losing the runtime type, so the override copies into a fresh LocalHttpTask
    /// (<see cref="HttpTask.CopyFromInternal"/> carries <c>Type</c> and every HTTP property).
    /// </summary>
    public override WorkflowTask Clone()
    {
        var cloned = new LocalHttpTask();
        cloned.CopyFromInternal(this);
        return cloned;
    }
}
