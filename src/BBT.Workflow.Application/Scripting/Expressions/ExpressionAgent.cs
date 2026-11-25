using System.Text.Json;
using BBT.Workflow.Scripting.Rules;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>
/// The <c>outcome</c> variable of an agent decision expression (<see cref="ExpressionSignatures.AgentDecision"/>): what
/// the agent run produced, read-only, with camelCase members like every other expression variable
/// (<c>outcome.decision</c>, <c>outcome.output.score</c>). Built from the <see cref="AgentRunOutcome"/> a C#
/// <c>IAgentDecisionMapping</c> receives.
/// </summary>
#pragma warning disable IDE1006 // camelCase members are the expression-language surface
public sealed class ExpressionAgentOutcome
{
    private readonly AgentRunOutcome _outcome;
    private RuleJsonDynamic? _output;

    /// <summary>Wraps <paramref name="outcome"/>; its output document must outlive the evaluation.</summary>
    public ExpressionAgentOutcome(AgentRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _outcome = outcome;
    }

    /// <summary>Whether the model returned a final answer.</summary>
    public bool completed => _outcome.Completed;

    /// <summary>The structured output (members read as <c>outcome.output.x</c>; a missing member reads as null).</summary>
    public RuleJsonDynamic output => _output ??= _outcome.Output is { ValueKind: not JsonValueKind.Undefined } element
        ? RuleJsonDynamic.Borrow(element)
        : RuleJsonDynamic.Empty;

    /// <summary>The structured output as compact JSON text (empty when there is none), for <c>Contains</c> checks.</summary>
    public string text => _outcome.Output is { ValueKind: not JsonValueKind.Undefined } element
        ? element.GetRawText()
        : string.Empty;

    /// <summary>The <c>decision</c> member of the output, when present.</summary>
    public string? decision => _outcome.ModelDecision;

    /// <summary>The <c>reason</c> member of the output, when present.</summary>
    public string? reason => _outcome.Reason;

    /// <summary>Why the run did not complete, when it did not.</summary>
    public string? failureReason => _outcome.FailureReason;
}

/// <summary>
/// The <c>candidates</c> variable of an agent decision expression: the transitions the agent may choose, in declaration
/// order. <c>candidates.has("approve")</c>, <c>candidates.keys</c>, <c>candidates.count</c>, <c>candidates[0].key</c>,
/// <c>candidates.find("approve").when</c>.
/// </summary>
public sealed class ExpressionAgentCandidates
{
    private readonly ExpressionAgentCandidate[] _items;

    /// <summary>Wraps <paramref name="candidates"/>.</summary>
    public ExpressionAgentCandidates(IReadOnlyList<AgentCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _items = candidates.Select(c => new ExpressionAgentCandidate(c)).ToArray();
    }

    /// <summary>Number of candidates.</summary>
    public int count => _items.Length;

    /// <summary>The candidate transition keys, in declaration order.</summary>
    // An array on purpose: decision expressions use candidates.keys[0] and keys.Length.
    public string[] keys => _items.Select(c => c.key).ToArray(); // skipcq: CS-P1026

    /// <summary>The candidate at <paramref name="index"/>.</summary>
    public ExpressionAgentCandidate this[int index] => _items[index];

    /// <summary>True when <paramref name="key"/> is a candidate transition key (ordinal).</summary>
    public bool has(string? key) => find(key) is not null;

    /// <summary>The candidate with transition key <paramref name="key"/>, or null.</summary>
    public ExpressionAgentCandidate? find(string? key) =>
        key is null ? null : Array.Find(_items, c => string.Equals(c.key, key, StringComparison.Ordinal));
}

/// <summary>One element of <see cref="ExpressionAgentCandidates"/>.</summary>
public sealed class ExpressionAgentCandidate
{
    internal ExpressionAgentCandidate(AgentCandidate candidate)
    {
        key = candidate.Key;
        when = candidate.When;
        whenSource = candidate.WhenSource;
    }

    /// <summary>Transition key.</summary>
    public string key { get; }

    /// <summary>When the transition applies, as the model reads it.</summary>
    public string when { get; }

    /// <summary>Where <see cref="when"/> came from (<c>hintsMapping</c>, <c>hint</c>, <c>description</c>, <c>label</c>, <c>key</c>).</summary>
    public string whenSource { get; }
}
#pragma warning restore IDE1006
