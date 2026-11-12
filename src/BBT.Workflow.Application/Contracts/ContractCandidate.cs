using BBT.Workflow.Definitions;

namespace BBT.Workflow.Contracts;

/// <summary>
/// One rule-guarded entry of a contract slot (a schema or view selection), projected onto a shape
/// shared by functions and transitions.
/// </summary>
/// <param name="Rule">The condition that must hold for the entry to win; null marks the rule-less fallback.</param>
/// <param name="Reference">The component reference the entry points at.</param>
/// <param name="LoadData">A view entry's <c>loadData</c> flag; null for schema entries.</param>
public sealed record ContractCandidate(ScriptCode? Rule, Reference Reference, bool? LoadData);
