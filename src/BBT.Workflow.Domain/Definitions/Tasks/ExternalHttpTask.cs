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
/// <c>ExternalHttpTaskExecutor</c>.
/// </remarks>
public sealed class ExternalHttpTask : HttpTask
{
    private ExternalHttpTask()
    {
    }

    [JsonConstructor]
    private ExternalHttpTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.ExternalHttp).ToString();
    }

    public static ExternalHttpTask Create(
        JsonElement config)
    {
        return new ExternalHttpTask(config);
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static new ExternalHttpTask CreateEmpty()
    {
        return new ExternalHttpTask();
    }

    /// <summary>
    /// Creates a deep copy of the current ExternalHttpTask instance. The base
    /// <see cref="HttpTask.CloneTyped"/> would materialize a plain <see cref="HttpTask"/>,
    /// losing the runtime type, so the override copies into a fresh ExternalHttpTask
    /// (<see cref="HttpTask.CopyFromInternal"/> carries <c>Type</c> and every HTTP property).
    /// </summary>
    public override WorkflowTask Clone()
    {
        var cloned = new ExternalHttpTask();
        cloned.CopyFromInternal(this);
        return cloned;
    }
}
