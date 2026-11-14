using System.Text.Json;

namespace BBT.Workflow.Agents;

/// <summary>
/// Tool approval round trip of an AiAgent state (issue #1105, phase 2). A run that stops on a tool marked
/// <c>requiresApproval</c> writes the held calls into its <c>$agent</c> summary
/// (<c>pendingApprovals</c>) and takes <c>decision.onApprovalRequest</c> — to any state the author chose.
/// When the instance enters the agent state again, the entering transition's body carries the answer under
/// <c>approval</c>, and the conversation resumes from its session instead of starting a new prompt:
/// <code>
/// { "approval": { "approved": true } }                                   // every held call
/// { "approval": [ { "callId": "call_1", "approved": false, "reason": "limit exceeded" } ] }
/// </code>
/// A held call without an answer is denied ("no approval answer was given"). Calls the model made in the
/// same turn that need no approval are approved automatically.
/// </summary>
public static class AgentApprovalSupport
{
    /// <summary>Body property that carries the answer.</summary>
    public const string ApprovalProperty = "approval";

    /// <summary>
    /// Reads the calls a previous run of <paramref name="state"/> held for approval, from the <c>$agent</c>
    /// summary under <paramref name="outputPath"/>. Empty when the last run did not stop for approval.
    /// </summary>
    public static IReadOnlyList<AgentPendingApproval> ReadPending(JsonElement? data, string outputPath, string state)
    {
        var path = outputPath.TrimStart('$').Trim('.');
        var summary = AgentTemplateRenderer.Navigate(data, string.IsNullOrEmpty(path) ? "$agent" : $"{path}.$agent");
        if (summary is not { ValueKind: JsonValueKind.Object } s ||
            !s.TryGetProperty("status", out var status) || status.GetString() != "ApprovalRequested" ||
            !s.TryGetProperty("state", out var owner) || owner.GetString() != state ||
            !s.TryGetProperty("pendingApprovals", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var pending = new List<AgentPendingApproval>();
        foreach (var item in list.EnumerateArray())
        {
            var requestId = item.TryGetProperty("requestId", out var r) ? r.GetString() : null;
            var callId = item.TryGetProperty("callId", out var c) ? c.GetString() : null;
            var tool = item.TryGetProperty("tool", out var n) ? n.GetString() : null;
            if (requestId is null || callId is null || tool is null)
                continue;
            var arguments = item.TryGetProperty("arguments", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            var requiresConfirmation = !item.TryGetProperty("requiresConfirmation", out var rc) || rc.ValueKind != JsonValueKind.False;
            pending.Add(new AgentPendingApproval(requestId, callId, tool, arguments, requiresConfirmation));
        }

        return pending;
    }

    /// <summary>Reads one string member of the <c>$agent</c> summary under <paramref name="outputPath"/>.</summary>
    public static string? ReadSummaryString(JsonElement? data, string outputPath, string member)
    {
        var path = outputPath.TrimStart('$').Trim('.');
        var value = AgentTemplateRenderer.Navigate(data, string.IsNullOrEmpty(path) ? $"$agent.{member}" : $"{path}.$agent.{member}");
        return value is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }

    /// <summary>Pairs every held call with its answer from the entering transition's body.</summary>
    public static IReadOnlyList<AgentApprovalAnswer> MatchAnswers(IReadOnlyList<AgentPendingApproval> pending, JsonElement? body)
    {
        var answers = new List<(string? CallId, bool Approved, string? Reason)>();
        if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty(ApprovalProperty, out var approval))
        {
            var items = approval.ValueKind == JsonValueKind.Array ? approval.EnumerateArray().ToList() : [approval];
            foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var approved = item.TryGetProperty("approved", out var ok) && ok.ValueKind == JsonValueKind.True;
                var callId = item.TryGetProperty("callId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                var reason = item.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
                answers.Add((callId, approved, reason));
            }
        }

        return pending.Select(p =>
        {
            if (!p.RequiresConfirmation)
                return new AgentApprovalAnswer(p, true, null);

            var index = answers.FindIndex(a => a.CallId == p.CallId);
            if (index < 0)
                index = answers.FindIndex(a => a.CallId is null);
            return index < 0
                ? new AgentApprovalAnswer(p, false, "no approval answer was given")
                : new AgentApprovalAnswer(p, answers[index].Approved, answers[index].Reason);
        }).ToList();
    }
}
