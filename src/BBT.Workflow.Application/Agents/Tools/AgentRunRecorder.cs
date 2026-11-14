using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Agents.Tools;

/// <summary>
/// Collects the tool calls of one run and enforces the tool budgets: the call ceiling, the per-tool
/// timeout, the argument schema and the result size the model receives. Rows stay in memory and
/// are written with the run.
/// </summary>
public sealed class AgentRunRecorder(Guid runId, Guid instanceId, EffectiveAgentPolicy policy, bool persistResults, DateTime startedAtUtc, ILogger logger)
{
    private readonly object _gate = new();
    private readonly List<InstanceAgentToolCall> _calls = [];
    private readonly Dictionary<string, (AgentToolKind Kind, string? BindingRef)> _tools = new(StringComparer.Ordinal);
    private int _sequence;

    /// <summary>Run id.</summary>
    public Guid RunId => runId;

    /// <summary>Recorded calls in order.</summary>
    public IReadOnlyList<InstanceAgentToolCall> Calls
    {
        get // skipcq: CS-P1026 — a snapshot taken under the lock; tools record calls in parallel
        {
            lock (_gate)
                return _calls.ToList();
        }
    }

    /// <summary>Calls that ran (not rejected).</summary>
    public int ExecutedCount
    {
        get
        {
            lock (_gate)
                return _calls.Count(c => c.Status != AgentToolCallStatus.Rejected);
        }
    }

    /// <summary>Takes the next call sequence and reports whether the tool-call budget is already spent.</summary>
    private (int Sequence, bool OverBudget) Reserve()
    {
        lock (_gate)
            return (++_sequence, _calls.Count(c => c.Status != AgentToolCallStatus.Rejected) >= policy.MaxToolCalls);
    }

    /// <summary>Runs the body under its timeout, or rejects the call (budget spent, arguments invalid or missing).</summary>
    private async Task<AgentToolResult> InvokeAsync(
        bool overBudget,
        JsonElement parameters,
        Func<JsonElement, string?>? validateArguments,
        AgentToolInvocation invocation,
        int? timeoutSeconds,
        Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> body,
        CancellationToken cancellationToken)
    {
        if (overBudget)
        {
            return new AgentToolResult(AgentToolCallStatus.Rejected, string.Empty,
                $"tool call budget exhausted ({policy.MaxToolCalls}); answer with what you have");
        }

        if (validateArguments is not null && validateArguments(invocation.Arguments) is { } invalid)
            return new AgentToolResult(AgentToolCallStatus.Rejected, string.Empty, $"invalid arguments: {invalid}");

        if (validateArguments is null && MissingRequired(parameters, invocation.Arguments) is { } missing)
            return new AgentToolResult(AgentToolCallStatus.Rejected, string.Empty, $"missing required argument '{missing}'");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var seconds = Math.Max(1, Math.Min(timeoutSeconds ?? policy.ToolTimeoutSeconds, policy.ToolTimeoutSeconds));
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await body(invocation, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AgentToolResult(AgentToolCallStatus.Timeout, string.Empty, $"tool did not answer within {seconds}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) // skipcq: CS-R1008 — a failing tool is an answer to the model, whatever it throws
        {
            return new AgentToolResult(AgentToolCallStatus.Failed, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Wraps a tool body with budgets, timing, truncation and journaling. <paramref name="validateArguments"/>
    /// answers null for arguments that match the tool's parameter schema, else the errors; a call that does
    /// not match is rejected without running and the model gets the errors. Without it only the
    /// <c>required</c> names are checked.
    /// </summary>
    public Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> Wrap(
        string name,
        AgentToolKind kind,
        string? bindingRef,
        JsonElement parameters,
        int? timeoutSeconds,
        int? resultMaxChars,
        Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> body,
        Func<JsonElement, string?>? validateArguments = null)
    {
        lock (_gate)
            _tools[name] = (kind, bindingRef);
        return async (invocation, cancellationToken) =>
        {
            var (sequence, overBudget) = Reserve();

            var started = Stopwatch.GetTimestamp();
            var startedAt = DateTime.UtcNow;
            var result = await InvokeAsync(overBudget, parameters, validateArguments, invocation, timeoutSeconds, body, cancellationToken);

            var max = Math.Max(1, Math.Min(resultMaxChars ?? policy.ResultMaxChars, policy.ResultMaxChars));
            var content = result.Content.Length > max ? result.Content[..max] + "…[truncated]" : result.Content;
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var row = new InstanceAgentToolCall(Guid.NewGuid())
            {
                RunId = runId,
                InstanceId = instanceId,
                Sequence = sequence,
                Iteration = invocation.Iteration,
                CallId = Truncate(invocation.CallId, 64),
                Name = Truncate(name, 64),
                Kind = kind,
                BindingRef = bindingRef is null ? null : Truncate(bindingRef, 300),
                IdempotencyKey = InstanceAgentToolCall.CreateIdempotencyKey(runId, invocation.CallId),
                Arguments = invocation.Arguments.GetRawText(),
                Result = persistResults && result.IsSuccess ? AsJson(result.Content) : null,
                Status = result.Status,
                Error = result.Error is null ? null : Truncate(result.Error, 400),
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
                DurationMs = elapsed
            };

            lock (_gate)
                _calls.Add(row);

            logger.AgentToolInvoked(runId, name, kind.ToString(), result.Status.ToString(), elapsed);
            return result with { Content = content };
        };
    }

    /// <summary>
    /// Records a call that did not run in this run: waiting for approval
    /// (<see cref="AgentToolCallStatus.ApprovalPending"/>) or answered with a denial
    /// (<see cref="AgentToolCallStatus.Denied"/>).
    /// </summary>
    public void RecordApproval(AgentPendingApproval pending, AgentToolCallStatus status, string? reason)
    {
        lock (_gate)
        {
            var (kind, bindingRef) = _tools.TryGetValue(pending.ToolName, out var known) ? known : (AgentToolKind.Task, null);
            var now = DateTime.UtcNow;
            _calls.Add(new InstanceAgentToolCall(Guid.NewGuid())
            {
                RunId = runId,
                InstanceId = instanceId,
                Sequence = ++_sequence,
                Iteration = 0,
                CallId = Truncate(pending.CallId, 64),
                Name = Truncate(pending.ToolName, 64),
                Kind = kind,
                BindingRef = bindingRef is null ? null : Truncate(bindingRef, 300),
                IdempotencyKey = InstanceAgentToolCall.CreateIdempotencyKey(runId, pending.CallId),
                Arguments = pending.Arguments.GetRawText(),
                Status = status,
                Error = reason is null ? null : Truncate(reason, 400),
                StartedAt = now,
                FinishedAt = now,
                DurationMs = 0
            });
        }
    }

    private static string? MissingRequired(JsonElement schema, JsonElement args)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in required.EnumerateArray())
        {
            var name = item.GetString();
            if (name is null)
                continue;
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return name;
            }
        }

        return null;
    }

    private static string AsJson(string content)
    {
        try
        {
            using var _ = JsonDocument.Parse(content);
            return content;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { text = content });
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
