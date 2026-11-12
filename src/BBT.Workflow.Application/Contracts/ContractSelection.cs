namespace BBT.Workflow.Contracts;

/// <summary>
/// The winning entry of a contract slot after rule evaluation.
/// </summary>
/// <param name="Reference">The component reference the winning entry points at.</param>
/// <param name="MatchedByRule">True when a rule selected the entry; false when it won as the rule-less fallback.</param>
/// <param name="LoadData">The winning view entry's <c>loadData</c> flag; null for schema entries.</param>
public sealed record ContractSelection(Reference Reference, bool MatchedByRule, bool? LoadData);
