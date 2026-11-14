using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Agents;

/// <summary>Candidate resolution and decision schema helpers (issue #1105).</summary>
public static class AgentDecisionSupport
{
    /// <summary>Name of the decision member the runtime adds to the output schema in model mode.</summary>
    public const string DecisionProperty = "decision";

    /// <summary>Name of the reason member the runtime adds to the output schema in model mode.</summary>
    public const string ReasonProperty = "reason";

    /// <summary>
    /// Builds the candidates of <paramref name="state"/> with their "when" text. Order of sources:
    /// hint mapping, <c>decision.hints</c>, transition description, first label, key.
    /// </summary>
    public static IReadOnlyList<AgentCandidate> BuildCandidates(State state, IDictionary<string, string>? mappedHints)
    {
        var hints = state.Agent?.Decision?.Hints;
        var list = new List<AgentCandidate>();
        foreach (var transition in AgentCandidateSet.Resolve(state))
        {
            if (mappedHints is not null && mappedHints.TryGetValue(transition.Key, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                list.Add(new AgentCandidate(transition.Key, mapped, "hintsMapping"));
            else if (hints is not null && hints.TryGetValue(transition.Key, out var hint) && !string.IsNullOrWhiteSpace(hint))
                list.Add(new AgentCandidate(transition.Key, hint, "hint"));
            else if (!string.IsNullOrWhiteSpace(transition.Description))
                list.Add(new AgentCandidate(transition.Key, transition.Description!, "description"));
            else if (transition.Labels.FirstOrDefault() is { } label && !string.IsNullOrWhiteSpace(label.Label))
                list.Add(new AgentCandidate(transition.Key, label.Label, "label"));
            else
                list.Add(new AgentCandidate(transition.Key, transition.Key, "key"));
        }

        return list;
    }

    /// <summary>
    /// In model mode, adds <c>decision</c> (enum of candidate keys) and <c>reason</c> to the output schema
    /// and makes both required.
    /// </summary>
    public static JsonElement AugmentSchema(JsonElement schema, AgentDecisionMode mode, IReadOnlyList<AgentCandidate> candidates)
    {
        if (mode != AgentDecisionMode.Model || candidates.Count == 0)
            return schema;

        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        var properties = node["properties"] as JsonObject ?? new JsonObject();
        node["properties"] = properties;

        var enumValues = new JsonArray(candidates.Select(c => (JsonNode)JsonValue.Create(c.Key)!).ToArray());
        properties[DecisionProperty] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = enumValues,
            ["description"] = "The key of the option that applies."
        };
        properties[ReasonProperty] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "One sentence explaining the decision."
        };

        var required = node["required"] as JsonArray ?? new JsonArray();
        foreach (var name in new[] { DecisionProperty, ReasonProperty })
        {
            if (required.All(r => r?.GetValue<string>() != name))
                required.Add(name);
        }

        node["required"] = required;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Output member a handoff router names its participant in.</summary>
    public const string HandoffProperty = "handoff";

    /// <summary>
    /// Adds the required <c>handoff</c> enum (participant names) and <c>handoffReason</c> to a handoff
    /// router's output schema.
    /// </summary>
    public static JsonElement AugmentHandoff(JsonElement schema, IReadOnlyList<AgentParticipant> participants)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        var properties = node["properties"] as JsonObject ?? new JsonObject();
        node["properties"] = properties;
        properties[HandoffProperty] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(participants.Select(p => (JsonNode)JsonValue.Create(p.Name!)!).ToArray()),
            ["description"] = "The participant that handles this request."
        };
        properties["handoffReason"] = new JsonObject { ["type"] = "string", ["description"] = "One sentence explaining the choice." };

        var required = node["required"] as JsonArray ?? new JsonArray();
        foreach (var name in new[] { HandoffProperty, "handoffReason" })
        {
            if (required.All(r => r?.GetValue<string>() != name))
                required.Add(name);
        }

        node["required"] = required;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>The participants a handoff router chooses from, appended to its prompt.</summary>
    public static string DescribeParticipants(IReadOnlyList<AgentParticipant> participants)
    {
        var lines = participants.Select(p => $"- \"{p.Name}\": {p.Description ?? p.Name}");
        return "\n\nChoose exactly one participant for the \"handoff\" field:\n" + string.Join("\n", lines);
    }

    /// <summary>The decision options appended to the user prompt in model mode.</summary>
    public static string DescribeCandidates(IReadOnlyList<AgentCandidate> candidates)
    {
        var lines = candidates.Select(c => $"- \"{c.Key}\": {c.When}");
        return "\n\nChoose exactly one option for the \"decision\" field:\n" + string.Join("\n", lines);
    }

    /// <summary>Reads a string member of the output.</summary>
    public static string? ReadString(JsonElement? output, string name) =>
        output is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
