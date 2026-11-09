using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Agents.Tools;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Agents;

/// <summary>
/// Runs the agent of an <see cref="StateType.AiAgent"/> state entry (issue #1105) and leaves its decision
/// on the hop's context for the automatic-transition step. Order of work:
/// resolve the agent (component cache) → candidates and their "when" texts → prompt → session read
/// (one primary-key lookup) → tools → provider run (no database connection is used while waiting on it)
/// → one instance-data write (<c>output.path</c> + <c>$agent</c> summary) → session upsert (one statement)
/// → run, tool-call and decision rows added to the hop's unit of work.
/// </summary>
public sealed class AgentStateRunner(
    IComponentCacheStore componentCacheStore,
    ICurrentSchema currentSchema,
    IOptions<AiAgentOptions> options,
    IAgentRuntime runtime,
    AgentToolCatalog toolCatalog,
    AgentRunLimiter limiter,
    IAgentRunJournal journal,
    IAgentSessionStore sessionStore,
    IInstanceDataWriteService dataWriteService,
    ICallerRoleResolver callerRoleResolver,
    IScriptEngine scriptEngine,
    ILogger<AgentStateRunner> logger)
{
    private const string AgentSummaryProperty = "$agent";

    /// <summary>Runs the agent and returns the in-hop decision, or a failure for the error boundary.</summary>
    public async Task<Result<AgentHopDecision>> RunAsync(
        TransitionExecutionContext context,
        ScriptContext scriptContext,
        Guid? transitionId,
        CancellationToken cancellationToken)
    {
        var state = context.Target!;
        var binding = state.Agent!;
        var decisionSpec = binding.Decision!;
        var fallback = decisionSpec.Fallback!;
        var reserved = decisionSpec.ReservedKeys.ToHashSet(StringComparer.Ordinal);
        var settings = options.Value;
        var runId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var instance = context.Instance;

        using var activity = MafActivity.Start(state.Key, runId);

        var entryNo = await journal.CountRunsAsync(instance.Id, state.Key, cancellationToken) + 1;
        var run = new InstanceAgentRun(runId)
        {
            InstanceId = instance.Id,
            TransitionId = transitionId,
            State = state.Key,
            EntryNo = entryNo,
            ExecutionKey = InstanceAgentRun.CreateExecutionKey(transitionId, state.Key, entryNo),
            AgentKey = binding.Agent?.Key ?? string.Empty,
            AgentDomain = binding.Agent?.Domain ?? context.Domain,
            AgentVersion = binding.Agent?.Version ?? string.Empty,
            RuntimeKey = AgentRuntimeKeys.Maf,
            ProviderKey = string.Empty,
            StartedAt = startedAt,
            CreatedAt = startedAt,
            Attempts = 0
        };

        if (!settings.Enabled)
        {
            logger.AgentRunSkippedDisabled(state.Key, instance.Id, fallback);
            return await FinishAsync(context, run, [], AgentRunStatus.Failed, "AI agents are disabled in this environment",
                decisionSpec, [], fallback, AgentDecisionSource.Disabled, null, clock, null, cancellationToken);
        }

        // 1. Resolve the agent component (cache store, generation-token keyed; no database read on a warm cache).
        var agentResult = await ResolveAgentAsync(binding.Agent!, cancellationToken);
        if (!agentResult.IsSuccess)
            return Result<AgentHopDecision>.Fail(agentResult.Error);
        var agent = agentResult.Value!;
        run.AgentKey = agent.Key;
        run.AgentDomain = agent.Domain;
        run.AgentVersion = agent.Version;
        run.RuntimeKey = agent.Runtime;
        run.ProviderKey = agent.Provider;

        if (!settings.Providers.TryGetValue(agent.Provider, out var profile))
        {
            return Result<AgentHopDecision>.Fail(Error.Failure(
                WorkflowErrorCodes.AgentProviderNotConfigured,
                $"Agent '{agent.Key}' uses provider profile '{agent.Provider}', which is not configured (AiAgent:Providers:{agent.Provider})."));
        }

        var policy = EffectiveAgentPolicy.Resolve(agent.Policy, binding.Policy, settings);
        run.Model = profile.Model;

        // 2. Re-entry ceiling: no provider call, the state leaves through onExhausted.
        if (policy.MaxEntries is { } maxEntries && entryNo > maxEntries && !string.IsNullOrEmpty(decisionSpec.OnExhausted))
        {
            return await FinishAsync(context, run, [], AgentRunStatus.BudgetExceeded, $"entry {entryNo} exceeds policy.maxEntries ({maxEntries})",
                decisionSpec, [], decisionSpec.OnExhausted!, AgentDecisionSource.Exhausted, null, clock, null, cancellationToken);
        }

        var templateScope = new AgentTemplateRenderer.Scope(
            instance.LatestData?.Data.JsonElement, context.DataElement, instance.Id, instance.Key, state.Key);

        // 3. Candidates with their "when" texts.
        IDictionary<string, string>? mappedHints = null;
        if (decisionSpec.HintsMapping is { } hintsMapping)
        {
            var seed = AgentDecisionSupport.BuildCandidates(state, null);
            var mapper = await scriptEngine.CompileToInstanceAsync<IAgentDecisionHintMapping>(
                hintsMapping, flowScripts: context.Workflow.Scripts, cancellationToken: cancellationToken);
            mappedHints = await mapper.Handler(seed, scriptContext);
        }

        var candidates = AgentDecisionSupport.BuildCandidates(state, mappedHints);
        var mode = decisionSpec.EffectiveMode;

        // 4. Prompt and output schema.
        var userPrompt = binding.Prompt!.IsScript
            ? await (await scriptEngine.CompileToInstanceAsync<IAgentPromptMapping>(
                    binding.Prompt.ToScriptCode(), flowScripts: context.Workflow.Scripts, cancellationToken: cancellationToken))
                .Handler(scriptContext)
            : AgentTemplateRenderer.Render(binding.Prompt.Template ?? string.Empty, templateScope);
        if (mode == AgentDecisionMode.Model)
            userPrompt += AgentDecisionSupport.DescribeCandidates(candidates);

        var schema = AgentDecisionSupport.AugmentSchema(agent.Output!.Schema!.Value, mode, candidates);
        var instructions = agent.Instructions!.Resolved;
        var instructionsHash = AgentTemplateRenderer.Sha256(instructions);

        // 5. Session (one primary-key read).
        var memory = AgentMemory.Default.Overlay(agent.Memory).Overlay(binding.Memory);
        var memoryMode = memory.Mode ?? AgentMemoryMode.State;
        var useProviderSession = memory.Storage == AgentMemoryStorage.Provider && profile.SupportsServiceSessions;
        var scopeKey = ScopeKey(memoryMode, memory, context, agent, templateScope);
        AgentSessionSnapshot? snapshot = null;
        if (scopeKey is not null)
        {
            var read = await sessionStore.FindAsync(scopeKey, instance.Id, cancellationToken);
            if (read.Unreadable is { } why)
                logger.AgentSessionUnreadable(scopeKey, why);
            snapshot = read.Snapshot;

            if (snapshot is not null && ResetReason(snapshot, agent, instructionsHash, memory) is { } reason)
            {
                logger.AgentSessionReset(scopeKey, reason);
                snapshot = snapshot with { Messages = null, ProviderConversationId = null, MessageCount = 0 };
            }
            else if (snapshot is not null)
            {
                logger.AgentSessionResumed(scopeKey, snapshot.MessageCount);
                run.SessionResumed = true;
            }

            run.SessionScopeKey = scopeKey;
        }

        // 6. Tools.
        var rolesResult = await callerRoleResolver.ResolveRolesAsync(context.Headers, cancellationToken);
        var callerRoles = rolesResult.IsSuccess ? rolesResult.Value ?? [] : [];
        var audit = agent.Audit;
        var recorder = new AgentRunRecorder(runId, instance.Id, policy, audit?.PersistToolResults ?? true, startedAt, logger);
        var tools = await toolCatalog.BuildAsync(new AgentToolBuildContext(
            agent,
            (binding.Tools?.Exclude ?? []).ToHashSet(StringComparer.Ordinal),
            callerRoles,
            scriptContext,
            transitionId,
            recorder,
            Stub: false), cancellationToken);

        logger.AgentRunStarted(runId, agent.Key, agent.Version, state.Key, instance.Id, agent.Provider);

        // 7. Provider run, under the bulkhead.
        AgentRunResult result;
        using (var slot = limiter.TryAcquire())
        {
            result = slot is null
                ? AgentRunResult.Failed(AgentRunStatus.Failed, $"capacity: AiAgent:MaxConcurrentRuns ({settings.MaxConcurrentRuns}) reached")
                : await runtime.RunAsync(new AgentRunRequest(
                    runId,
                    agent.Key,
                    agent.Provider,
                    profile,
                    instructions,
                    userPrompt,
                    schema,
                    agent.Model,
                    tools,
                    policy,
                    snapshot?.Messages,
                    snapshot?.ProviderConversationId,
                    useProviderSession,
                    memory.MaxMessages ?? 40,
                    CaptureTranscript: (audit?.Transcript ?? AgentTranscriptMode.None) != AgentTranscriptMode.None), cancellationToken);
        }

        ApplyResult(run, result, recorder, audit, settings, schema, candidates, userPrompt, instructionsHash, tools);

        // 8. Decision.
        string? selected;
        AgentDecisionSource source;
        string? reasonText;
        if (result.Status == AgentRunStatus.Completed)
        {
            reasonText = AgentDecisionSupport.ReadString(result.Output, AgentDecisionSupport.ReasonProperty);
            switch (mode)
            {
                case AgentDecisionMode.Model:
                {
                    var chosen = AgentDecisionSupport.ReadString(result.Output, AgentDecisionSupport.DecisionProperty);
                    (selected, source, reasonText) = candidates.Any(c => c.Key == chosen)
                        ? (chosen, AgentDecisionSource.Model, reasonText)
                        : (fallback, AgentDecisionSource.Fallback, $"model returned '{chosen ?? "nothing"}', not a candidate");
                    break;
                }
                case AgentDecisionMode.Script:
                {
                    var mapper = await scriptEngine.CompileToInstanceAsync<IAgentDecisionMapping>(
                        decisionSpec.Mapping!, flowScripts: context.Workflow.Scripts, cancellationToken: cancellationToken);
                    var outcome = new AgentRunOutcome(true, result.Output,
                        AgentDecisionSupport.ReadString(result.Output, AgentDecisionSupport.DecisionProperty), reasonText, null);
                    var chosen = await mapper.Handler(outcome, candidates, scriptContext);
                    (selected, source, reasonText) = candidates.Any(c => c.Key == chosen)
                        ? (chosen, AgentDecisionSource.Script, reasonText)
                        : (fallback, AgentDecisionSource.Fallback, $"decision script returned '{chosen ?? "nothing"}', not a candidate");
                    break;
                }
                default:
                    selected = null;
                    source = AgentDecisionSource.Rules;
                    break;
            }
        }
        else
        {
            logger.AgentRunFailed(runId, instance.Id, result.Status.ToString(), result.FailureReason ?? "unknown");
            if (policy.OnProviderError == AgentProviderErrorAction.Fault &&
                result.Status is AgentRunStatus.Failed or AgentRunStatus.Timeout)
            {
                await journal.AddAsync(run, recorder.Calls, DecisionRow(run, mode, AgentDecisionSource.Fallback, null, result.FailureReason, candidates, false), cancellationToken);
                return Result<AgentHopDecision>.Fail(Error.Failure(WorkflowErrorCodes.AgentRunFailed, $"Agent run failed: {result.FailureReason}"));
            }

            selected = fallback;
            source = AgentDecisionSource.Fallback;
            reasonText = result.FailureReason;
        }

        // 9. One data write: the structured output (completed runs) and the $agent summary.
        await WriteOutputAsync(context, binding.Output!.Path!, result.Status == AgentRunStatus.Completed ? result.Output : null,
            Summary(run, selected, source, reasonText, recorder, result), cancellationToken);

        // 10. Session (one upsert statement), only after a completed run.
        if (scopeKey is not null && result.Status == AgentRunStatus.Completed)
            await SaveSessionAsync(scopeKey, memoryMode, memory, useProviderSession, agent, instructionsHash, instance.Id, runId, snapshot, result, context, cancellationToken);

        return await FinishAsync(context, run, recorder.Calls, run.Status, run.FailureReason, decisionSpec, candidates, selected,
            source, reasonText, clock, recorder, cancellationToken, written: true);
    }

    private async Task<Result<AgentHopDecision>> FinishAsync(
        TransitionExecutionContext context,
        InstanceAgentRun run,
        IReadOnlyList<InstanceAgentToolCall> calls,
        AgentRunStatus status,
        string? failure,
        AgentDecision decisionSpec,
        IReadOnlyList<AgentCandidate> candidates,
        string? selected,
        AgentDecisionSource source,
        string? reason,
        Stopwatch clock,
        AgentRunRecorder? recorder,
        CancellationToken cancellationToken,
        bool written = false)
    {
        run.Status = status;
        run.FailureReason = failure is null ? null : Truncate(failure, 400);
        run.FinishedAt = DateTime.UtcNow;
        run.DurationMs = clock.ElapsedMilliseconds;

        if (!written)
        {
            await WriteOutputAsync(context, context.Target!.Agent!.Output!.Path!, null,
                Summary(run, selected, source, reason, recorder, null), cancellationToken);
        }

        var isFallback = source is AgentDecisionSource.Fallback or AgentDecisionSource.Disabled or AgentDecisionSource.Exhausted;
        var mode = decisionSpec.EffectiveMode;
        var row = DecisionRow(run, mode, source, selected, reason, candidates, isFallback);
        await journal.AddAsync(run, calls, row, cancellationToken);

        if (status == AgentRunStatus.Completed)
            logger.AgentRunCompleted(run.Id, run.InstanceId, run.DurationMs, run.Iterations, run.ToolCallCount, run.TotalTokens);
        logger.AgentDecisionResolved(run.Id, run.InstanceId, run.State, selected, source.ToString());

        return Result<AgentHopDecision>.Ok(new AgentHopDecision(
            mode,
            selected,
            source,
            candidates.Select(c => c.Key).ToHashSet(StringComparer.Ordinal),
            decisionSpec.ReservedKeys.ToHashSet(StringComparer.Ordinal),
            decisionSpec.Fallback!,
            row));
    }

    private static InstanceAgentDecision DecisionRow(
        InstanceAgentRun run, AgentDecisionMode mode, AgentDecisionSource source, string? selected, string? reason,
        IReadOnlyList<AgentCandidate> candidates, bool isFallback) => new(Guid.NewGuid())
    {
        RunId = run.Id,
        InstanceId = run.InstanceId,
        State = run.State,
        Mode = mode,
        Source = source,
        SelectedTransition = selected,
        Reason = reason is null ? null : Truncate(reason, 2000),
        Candidates = JsonSerializer.Serialize(candidates.Select(c => new { key = c.Key, when = c.When, whenSource = c.WhenSource })),
        IsFallback = isFallback,
        CreatedAt = DateTime.UtcNow
    };

    private void ApplyResult(
        InstanceAgentRun run, AgentRunResult result, AgentRunRecorder recorder, AgentAudit? audit, AiAgentOptions settings,
        JsonElement schema, IReadOnlyList<AgentCandidate> candidates, string userPrompt, string instructionsHash,
        IReadOnlyList<AgentToolDefinition> tools)
    {
        run.Status = result.Status;
        run.FailureReason = result.FailureReason is null ? null : Truncate(result.FailureReason, 400);
        run.Attempts = result.Attempts;
        run.Model = result.Model ?? run.Model;
        run.ProviderResponseId = result.ProviderResponseId is null ? null : Truncate(result.ProviderResponseId, 128);
        run.FinishReason = result.FinishReason is null ? null : Truncate(result.FinishReason, 32);
        run.Iterations = result.Iterations;
        run.ToolCallCount = recorder.ExecutedCount;
        run.PromptTokens = result.PromptTokens;
        run.CompletionTokens = result.CompletionTokens;
        run.TotalTokens = result.PromptTokens + result.CompletionTokens;
        run.Response = result.Output?.GetRawText();

        var summaryOnly = (audit?.Transcript ?? AgentTranscriptMode.None) != AgentTranscriptMode.Full;
        run.Request = JsonSerializer.Serialize(new
        {
            instructionsHash,
            prompt = summaryOnly ? null : userPrompt,
            promptHash = AgentTemplateRenderer.Sha256(userPrompt),
            promptLength = userPrompt.Length,
            responseSchema = schema,
            toolNames = tools.Select(t => t.Name),
            candidates = candidates.Select(c => new { key = c.Key, whenSource = c.WhenSource })
        });

        if (result.Transcript is { } transcript)
        {
            var bytes = Gzip(System.Text.Encoding.UTF8.GetBytes(transcript));
            run.Transcript = bytes.Length <= settings.MaxTranscriptBytes ? bytes : null;
        }
    }

    private async Task WriteOutputAsync(
        TransitionExecutionContext context, string path, JsonElement? output, object summary, CancellationToken cancellationToken)
    {
        var payload = output is { ValueKind: JsonValueKind.Object } o
            ? JsonNode.Parse(o.GetRawText())!.AsObject()
            : new JsonObject();
        payload[AgentSummaryProperty] = JsonSerializer.SerializeToNode(summary, JsonSerializerConstants.JsonOptions);

        JsonNode node = payload;
        var segments = path.TrimStart('$').Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
            node = new JsonObject { [segments[i]] = node };

        var delta = JsonData.FromElement(JsonSerializer.SerializeToElement(node));
        await dataWriteService.AppendAsync(context.Instance, delta, VersionStrategy.IncreasePatch, cancellationToken, context.Workflow);
        context.RefreshScriptContextInstance();
    }

    /// <summary>
    /// The <c>$agent</c> summary of one run. Every field carries a non-null value on purpose: instance data
    /// merges per key and a <c>null</c> never clears a stored key, so a field left null would keep the
    /// previous run's value (an exhausted run would report the tool calls and session of the run before it).
    /// Empty text, an empty array and <c>scope: "none"</c> stand for "nothing this run".
    /// </summary>
    private static object Summary(
        InstanceAgentRun run, string? selected, AgentDecisionSource source, string? reason,
        AgentRunRecorder? recorder, AgentRunResult? result) => new
    {
        runId = run.Id,
        status = run.Status.ToString(),
        decision = selected ?? string.Empty,
        decisionSource = source.ToString(),
        reason = reason ?? string.Empty,
        stopReason = run.FailureReason ?? result?.FinishReason ?? string.Empty,
        iterations = run.Iterations,
        attempts = run.Attempts,
        toolCalls = (recorder?.Calls ?? [])
            .Select(c => new { name = c.Name, ok = c.Status == AgentToolCallStatus.Completed, durationMs = c.DurationMs })
            .ToArray(),
        usage = new { promptTokens = run.PromptTokens, completionTokens = run.CompletionTokens, totalTokens = run.TotalTokens },
        session = run.SessionScopeKey is null
            ? new { scope = "none", resumed = false }
            : new { scope = ScopeKind(run.SessionScopeKey), resumed = run.SessionResumed }
    };

    private static string ScopeKind(string scopeKey) => scopeKey.StartsWith("k:", StringComparison.Ordinal) ? "key" : "instance";

    private async Task SaveSessionAsync(
        string scopeKey, AgentMemoryMode mode, AgentMemory memory, bool useProviderSession, Agent agent, string instructionsHash,
        Guid instanceId, Guid runId, AgentSessionSnapshot? snapshot, AgentRunResult result, TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var messages = result.Session;
        var count = result.SessionMessageCount;
        if (messages is { Length: > 0 } && messages.Length > settings.MaxSessionBytes)
        {
            logger.AgentSessionTrimmed(scopeKey, count, 0, messages.Length);
            messages = null;
            count = 0;
        }
        else if (result.SessionMessagesTrimmed > 0)
        {
            logger.AgentSessionTrimmed(scopeKey, count + result.SessionMessagesTrimmed, count, messages?.Length ?? 0);
        }

        var now = DateTime.UtcNow;
        var isInstanceScope = scopeKey.StartsWith("i:", StringComparison.Ordinal);
        var session = new AgentSession
        {
            ScopeKey = scopeKey,
            Mode = mode,
            Storage = useProviderSession ? AgentMemoryStorage.Provider : AgentMemoryStorage.Local,
            AgentKey = agent.Key,
            AgentVersion = agent.Version,
            InstructionsHash = instructionsHash,
            ProviderConversationId = result.ProviderConversationId,
            MessageCount = count,
            OwnerInstanceId = isInstanceScope ? instanceId : null,
            LastInstanceId = instanceId,
            LastRunId = runId,
            UpdatedAt = now,
            ExpiresAt = now.AddDays(memory.RetentionDays ?? 30)
        };

        var write = new AgentSessionWrite(scopeKey, session, useProviderSession ? null : messages, snapshot?.RowVersion);
        if (!await sessionStore.SaveAsync(write, instanceId, cancellationToken))
            logger.AgentSessionConflict(scopeKey, 1);
    }

    private static string? ResetReason(AgentSessionSnapshot snapshot, Agent agent, string instructionsHash, AgentMemory memory)
    {
        var resetOn = memory.ResetOn ?? ["agentVersionChange"];
        if (resetOn.Contains("agentVersionChange", StringComparer.OrdinalIgnoreCase) &&
            (!string.Equals(snapshot.AgentKey, agent.Key, StringComparison.Ordinal) ||
             !string.Equals(snapshot.AgentVersion, agent.Version, StringComparison.Ordinal)))
        {
            return $"agent changed from {snapshot.AgentKey}@{snapshot.AgentVersion} to {agent.Key}@{agent.Version}";
        }

        if (resetOn.Contains("instructionsChange", StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(snapshot.InstructionsHash, instructionsHash, StringComparison.Ordinal))
        {
            return "instructions changed";
        }

        return null;
    }

    private static string? ScopeKey(
        AgentMemoryMode mode, AgentMemory memory, TransitionExecutionContext context, Agent agent, AgentTemplateRenderer.Scope scope) =>
        mode switch
        {
            AgentMemoryMode.State => $"i:{context.InstanceId:N}:{context.WorkflowKey}:{context.Target!.Key}",
            AgentMemoryMode.Flow => $"i:{context.InstanceId:N}:{context.WorkflowKey}",
            AgentMemoryMode.Key when !string.IsNullOrWhiteSpace(memory.Key) =>
                $"k:{agent.Domain}:{agent.Key}:{AgentTemplateRenderer.Sha256(AgentTemplateRenderer.Render(memory.Key!, scope))}",
            _ => null
        };

    private async Task<Result<Agent>> ResolveAgentAsync(Reference reference, CancellationToken cancellationToken)
    {
        using (currentSchema.Change(RuntimeSysSchemaInfo.Agents))
        {
            var result = await componentCacheStore.GetAgentAsync(reference.Domain, reference.Key, reference.Version, cancellationToken);
            return result.IsSuccess
                ? result
                : Result<Agent>.Fail(Error.NotFound(WorkflowErrorCodes.AgentNotFound,
                    $"Agent component {reference.Domain}/{reference.Key}@{reference.Version} could not be resolved: {result.Error.Message}"));
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
            gzip.Write(data);
        return output.ToArray();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>The <c>Agent.Run</c> span.</summary>
    private static class MafActivity
    {
        private static readonly ActivitySource Source = new(TelemetryConstants.ActivitySources.AgentsRun);

        public static Activity? Start(string state, Guid runId)
        {
            var activity = Source.StartActivity($"Agent.Run/{state}");
            activity?.SetTag("vnext.agent.run_id", runId);
            return activity;
        }
    }
}
