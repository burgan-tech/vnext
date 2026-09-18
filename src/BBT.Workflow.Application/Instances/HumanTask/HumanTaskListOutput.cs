

namespace BBT.Workflow.Instances.HumanTask;

/// <summary>
/// The human-task list plus the one fact the JSON body cannot carry.
/// </summary>
/// <remarks>
/// The response body is and stays a bare JSON array: morph-idm deserializes
/// <c>List&lt;VnextHumanTaskResponse&gt;</c>, and wrapping it in an envelope would be a breaking
/// change, which standing policy prohibits. Truncation therefore travels as a response header,
/// and this type is how the app service tells the HTTP layer to set it. A silently capped list is
/// a wrong answer, not a shorter one.
/// </remarks>
public sealed class HumanTaskListOutput
{
    /// <summary>The rows to serialize, ordered newest first.</summary>
    public List<HumanTaskItemOutput> Items { get; init; } = [];

    /// <summary>True when a per-schema limit or the merged cap cut the result.</summary>
    public bool Truncated { get; init; }
}
