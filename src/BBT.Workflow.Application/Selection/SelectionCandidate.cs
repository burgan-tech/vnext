using BBT.Workflow.Definitions;

namespace BBT.Workflow.Selection;

/// <summary>
/// One entry of a rule-based selection, projected off whatever declared it (a function contract slot,
/// a transition's <c>schemas</c>, a state's <c>views</c>). An entry with no <see cref="Rule"/> is the
/// declared fallback and always matches.
/// </summary>
/// <param name="Rule">Optional selection rule, compiled to <c>IConditionMapping</c>.</param>
/// <param name="Reference">The component reference this entry points at.</param>
/// <param name="LoadData">
/// The entry's <c>loadData</c> flag. Null for schema entries, which carry no rendering hints.
/// </param>
public sealed record SelectionCandidate(ScriptCode? Rule, Reference Reference, bool? LoadData = null);

/// <summary>
/// The winning entry of a rule-based selection.
/// </summary>
/// <param name="Reference">The component reference the winning entry points at.</param>
/// <param name="MatchedByRule">
/// True when a rule selected this entry; false when it won as the rule-less fallback.
/// </param>
/// <param name="LoadData">The winning entry's <c>loadData</c> flag, when it carries one.</param>
public sealed record SelectionMatch(Reference Reference, bool MatchedByRule, bool? LoadData);
