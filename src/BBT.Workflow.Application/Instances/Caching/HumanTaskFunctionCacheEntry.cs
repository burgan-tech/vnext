using BBT.Workflow.Instances.HumanTask;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed-cache envelope for a <c>human-task</c> response, already filtered and projected for
/// the caller scope encoded in the key.
/// </summary>
/// <remarks>
/// The entry carries <c>humanTask.title</c> and <c>description</c>, which can be
/// customer-identifying. That is safe only because the key covers every input the authorization
/// decision reads — the caller's roles and identity, and the headers dynamic role grants resolve
/// <c>$.context.Headers.*</c> against. Widening the key's scope without widening what it covers
/// would turn this into a cross-caller disclosure.
/// </remarks>
public sealed class HumanTaskFunctionCacheEntry
{
    /// <summary>The rows as they were served.</summary>
    public List<HumanTaskItemOutput> Items { get; set; } = [];

    /// <summary>Whether the cached list was already truncated when it was built.</summary>
    public bool Truncated { get; set; }
}
