namespace BBT.Workflow.Functions.Contracts;

/// <summary>
/// The winning entry of a function contract slot after rule evaluation.
/// </summary>
/// <param name="Reference">The component reference the winning entry points at.</param>
/// <param name="MatchedByRule">
/// True when a rule selected this entry; false when it won as the rule-less fallback.
/// </param>
/// <param name="LoadData">
/// The winning view entry's <c>loadData</c> flag. Always null for schema slots, which carry no
/// rendering hints.
/// </param>
public sealed record FunctionContractResolution(
    Reference Reference,
    bool MatchedByRule,
    bool? LoadData);
