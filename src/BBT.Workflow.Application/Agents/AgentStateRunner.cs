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

        // 2. A conversation that stopped for approval resumes with the answers carried by the entering
        //    transition; a resume is not a new entry for the re-entry ceiling.
        var pendingApprovals = AgentApprovalSupport.ReadPending(instance.LatestData?.Data.JsonElement, binding.Output!.Path!, state.Key);
        var approvalAnswers = pendingApprovals.Count > 0
            ? AgentApprovalSupport.MatchAnswers(pendingApprovals, context.DataElement)
            : null;

        // 3. Re-entry ceiling: no provider call, the state leaves through onExhausted.
        if (approvalAnswers is null && policy.MaxEntries is { } maxEntries && entryNo > maxEntries && !string.IsNullOrEmpty(decisionSpec.OnExhausted))
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
        var basePrompt = binding.Prompt!.IsScript
            ? await (await scriptEngine.CompileToInstanceAsync<IAgentPromptMapping>(
                    binding.Prompt.ToScriptCode(), flowScripts: context.Workflow.Scripts, cancellationToken: cancellationToken))
                .Handler(scriptContext)
            : AgentTemplateRenderer.Render(binding.Prompt.Template ?? string.Empty, templateScope);
        basePrompt += AgentTemplateRenderer.RenderContext(binding.Prompt.Context, templateScope);

        var rolesResult = await callerRoleResolver.ResolveRolesAsync(context.Headers, cancellationToken);
        var callerRoles = rolesResult.IsSuccess ? rolesResult.Value ?? [] : [];
        var exclude = (binding.Tools?.Exclude ?? []).ToHashSet(StringComparer.Ordinal);

        // 4b. Multi-agent: participants run before the final agent (sequential / concurrent), or the lead
        //     routes to the participant that becomes the final agent (handoff). A resumed approval continues
        //     the final agent's conversation and runs no participant again.
        var team = TeamSummary.None;
        var userPrompt = basePrompt;
        if (binding.EffectiveMode != AgentTeamMode.Single)
        {
            var participants = binding.Agents ?? [];
            if (approvalAnswers is not null)
            {
                var resumedHandoff = AgentApprovalSupport.ReadSummaryString(instance.LatestData?.Data.JsonElement, binding.Output!.Path!, "handoffTo");
                if (binding.EffectiveMode == AgentTeamMode.Handoff &&
                    participants.FirstOrDefault(p => p.Name == resumedHandoff) is { Agent: { } resumedRef } &&
                    await ResolveAgentAsync(resumedRef, cancellationToken) is { IsSuccess: true } resumed)
                {
                    agent = resumed.Value!;
                    team = team with { HandoffTo = resumedHandoff };
                }
            }
            else
            {
                var teamRun = await RunTeamAsync(context, scriptContext, transitionId, binding, agent, basePrompt, entryNo, exclude, callerRoles, cancellationToken);
                team = teamRun.Summary;
                if (teamRun.FailureReason is { } teamFailure)
                {
                    return await FinishAsync(context, run, [], AgentRunStatus.Failed, teamFailure,
                        decisionSpec, [], fallback, AgentDecisionSource.Fallback, teamFailure, clock, null, cancellationToken, team: team);
                }

                userPrompt += teamRun.Context;
                agent = teamRun.FinalAgent ?? agent;
            }

            if (!ReferenceEquals(agent, agentResult.Value))
            {
                run.AgentKey = agent.Key;
                run.AgentDomain = agent.Domain;
                run.AgentVersion = agent.Version;
                run.RuntimeKey = agent.Runtime;
                run.ProviderKey = agent.Provider;
                if (!settings.Providers.TryGetValue(agent.Provider, out profile))
                {
                    var why = $"participant agent '{agent.Key}' uses provider profile '{agent.Provider}', which is not configured";
                    return await FinishAsync(context, run, [], AgentRunStatus.Failed, why,
                        decisionSpec, [], fallback, AgentDecisionSource.Fallback, why, clock, null, cancellationToken, team: team);
                }

                policy = EffectiveAgentPolicy.Resolve(agent.Policy, binding.Policy, settings);
                run.Model = profile.Model;
            }
        }

        if (mode == AgentDecisionMode.Model)
            userPrompt += AgentDecisionSupport.DescribeCandidates(candidates);
        var seedMessages = (binding.Prompt.Messages ?? [])
            .Select(m => new AgentSeedMessage(m.Role!.ToLowerInvariant(), AgentTemplateRenderer.Render(m.Content ?? string.Empty, templateScope)))
            .ToList();

        var schema = AgentDecisionSupport.AugmentSchema(agent.Output!.Schema!.Value, mode, candidates);
        var instructions = agent.Instructions!.Resolved;
        var instructionsHash = AgentTemplateRenderer.Sha256(instructions);

        // 5. Session (one primary-key read).
        var memory = AgentMemory.Default.Overlay(agent.Memory).Overlay(binding.Memory);
        var memoryMode = memory.Mode ?? AgentMemoryMode.State;
        var useProviderSession = memory.Storage == AgentMemoryStorage.Provider && profile.SupportsServiceSessions;
        var scopeKey = ScopeKey(memoryMode, memory, context, agent, templateScope);
        // Held tool calls are resumed from a session, so a state without memory still keeps one for the approval round trip.
        var approvalScopeKey = scopeKey ?? (useProviderSession ? null : $"i:{context.InstanceId:N}:{context.WorkflowKey}:{state.Key}");
        var readScopeKey = approvalAnswers is not null ? approvalScopeKey : scopeKey;
        AgentSessionSnapshot? snapshot = null;
        if (readScopeKey is not null)
        {
            var read = await sessionStore.FindAsync(readScopeKey, instance.Id, cancellationToken);
            if (read.Unreadable is { } why)
                logger.AgentSessionUnreadable(readScopeKey, why);
            snapshot = read.Snapshot;

            if (approvalAnswers is null && snapshot is not null && ResetReason(snapshot, agent, instructionsHash, memory) is { } reason)
            {
                logger.AgentSessionReset(readScopeKey, reason);
                snapshot = snapshot with { Messages = null, ProviderConversationId = null, MessageCount = 0 };
            }
            else if (snapshot is not null)
            {
                logger.AgentSessionResumed(readScopeKey, snapshot.MessageCount);
                run.SessionResumed = true;
            }

            run.SessionScopeKey = readScopeKey;
        }

        // 6. Tools.
        var audit = agent.Audit;
        var recorder = new AgentRunRecorder(runId, instance.Id, policy, audit?.PersistToolResults ?? true, startedAt, logger);
        var tools = await toolCatalog.BuildAsync(new AgentToolBuildContext(
            agent,
            exclude,
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
                    CaptureTranscript: (audit?.Transcript ?? AgentTranscriptMode.None) != AgentTranscriptMode.None,
                    Compaction: memory.Compaction ?? AgentMemoryCompaction.Trim,
                    SeedMessages: seedMessages,
                    ApprovalAnswers: approvalAnswers,
                    FullTranscript: audit?.Transcript == AgentTranscriptMode.Full), cancellationToken);
        }

        foreach (var denied in approvalAnswers?.Where(a => !a.Approved && a.Pending.RequiresConfirmation) ?? [])
            recorder.RecordApproval(denied.Pending, AgentToolCallStatus.Denied, denied.Reason);

        ApplyResult(run, result, recorder, audit, settings, schema, candidates, userPrompt, instructionsHash, tools);

        // 8. Decision.
        string? selected;
        AgentDecisionSource source;
        string? reasonText;
        if (result.Status == AgentRunStatus.ApprovalRequested)
        {
            foreach (var held in result.PendingApprovals.Where(a => a.RequiresConfirmation))
                recorder.RecordApproval(held, AgentToolCallStatus.ApprovalPending, null);
            (selected, source, reasonText) = string.IsNullOrEmpty(decisionSpec.OnApprovalRequest)
                ? (fallback, AgentDecisionSource.Fallback, $"{result.FailureReason}, but the state declares no decision.onApprovalRequest")
                : (decisionSpec.OnApprovalRequest, AgentDecisionSource.ApprovalRequest, result.FailureReason);
        }
        else if (result.Status == AgentRunStatus.Completed)
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
            Summary(run, selected, source, reasonText, recorder, result, team), cancellationToken);

        // 10. Session (one upsert statement): after a completed run, and after a run held for approval
        //     (the resume needs it, whatever the memory mode).
        if (scopeKey is not null && result.Status == AgentRunStatus.Completed)
            await SaveSessionAsync(scopeKey, memoryMode, memory, useProviderSession, agent, instructionsHash, instance.Id, runId, snapshot, result, context, cancellationToken);
        else if (approvalScopeKey is not null && result.Status == AgentRunStatus.ApprovalRequested)
            await SaveSessionAsync(approvalScopeKey, scopeKey is null ? AgentMemoryMode.None : memoryMode, memory, useProviderSession, agent, instructionsHash, instance.Id, runId,
                snapshot, result, context, cancellationToken);

        return await FinishAsync(context, run, recorder.Calls, run.Status, run.FailureReason, decisionSpec, candidates, selected,
            source, reasonText, clock, recorder, cancellationToken, written: true, team: team);
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
        bool written = false,
        TeamSummary? team = null)
    {
        run.Status = status;
        run.FailureReason = failure is null ? null : Truncate(failure, 400);
        run.FinishedAt = DateTime.UtcNow;
        run.DurationMs = clock.ElapsedMilliseconds;

        if (!written)
        {
            await WriteOutputAsync(context, context.Target!.Agent!.Output!.Path!, null,
                Summary(run, selected, source, reason, recorder, null, team ?? TeamSummary.None), cancellationToken);
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
        AgentRunRecorder? recorder, AgentRunResult? result, TeamSummary team) => new
    {
        runId = run.Id,
        state = run.State,
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
            : new { scope = ScopeKind(run.SessionScopeKey), resumed = run.SessionResumed },
        participants = team.Participants
            .Select(p => new { name = p.Participant, agentKey = p.AgentKey, runId = p.Id, status = p.Status.ToString() })
            .ToArray(),
        handoffTo = team.HandoffTo ?? string.Empty,
        pendingApprovals = (result?.PendingApprovals ?? [])
            .Select(a => new { requestId = a.RequestId, callId = a.CallId, tool = a.ToolName, arguments = a.Arguments, requiresConfirmation = a.RequiresConfirmation })
            .ToArray()
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

    /// <summary>Participants of one state entry, for the <c>$agent</c> summary.</summary>
    private sealed record TeamSummary(IReadOnlyList<InstanceAgentRun> Participants, string? HandoffTo)
    {
        public static readonly TeamSummary None = new([], null);
    }

    /// <summary>What the team phase hands to the final run.</summary>
    private sealed record TeamOutcome(TeamSummary Summary, string Context, Agent? FinalAgent, string? FailureReason);

    /// <summary>
    /// Runs the participants of a multi-agent state. Sequential: in order, each prompt carrying the outputs
    /// before it; concurrent: in parallel on the same prompt. Both return the outputs as
    /// <c>&lt;data name="participant:…"&gt;</c> blocks for the lead, which stays the final agent. Handoff: the lead
    /// runs as a router whose output names a participant; that participant becomes the final agent. A
    /// participant that fails is passed on as <c>{"error": …}</c>; a router that fails or names no
    /// participant fails the team (the state takes its fallback). Participant rows join the hop's unit of
    /// work after every run finished, one at a time (the context is not thread-safe).
    /// </summary>
    private async Task<TeamOutcome> RunTeamAsync(
        TransitionExecutionContext context, ScriptContext scriptContext, Guid? transitionId, AgentStateBinding binding,
        Agent lead, string basePrompt, int entryNo, IReadOnlySet<string> exclude, IReadOnlyCollection<string> callerRoles,
        CancellationToken cancellationToken)
    {
        var participants = binding.Agents ?? [];
        var finished = new List<(InstanceAgentRun Run, IReadOnlyList<InstanceAgentToolCall> Calls, JsonElement? Output)>();

        async Task<TeamOutcome> CompleteAsync(string contextText, Agent? finalAgent, string? failure, string? handoffTo)
        {
            foreach (var (row, calls, _) in finished)
                await journal.AddAsync(row, calls, null, cancellationToken);
            return new TeamOutcome(new TeamSummary(finished.Select(f => f.Run).ToList(), handoffTo), contextText, finalAgent, failure);
        }

        switch (binding.EffectiveMode)
        {
            case AgentTeamMode.Sequential:
            {
                var carried = string.Empty;
                foreach (var participant in participants)
                {
                    var one = await RunAuxiliaryAsync(context, scriptContext, transitionId, binding, participant.Name!, participant.Agent!,
                        basePrompt + carried, null, entryNo, exclude, callerRoles, cancellationToken);
                    finished.Add(one);
                    carried += ParticipantBlock(one.Run, one.Output);
                }

                return await CompleteAsync(carried, null, null, null);
            }
            case AgentTeamMode.Concurrent:
            {
                var runs = await Task.WhenAll(participants.Select(participant =>
                    RunAuxiliaryAsync(context, scriptContext, transitionId, binding, participant.Name!, participant.Agent!,
                        basePrompt, null, entryNo, exclude, callerRoles, cancellationToken)));
                finished.AddRange(runs);
                return await CompleteAsync(string.Concat(runs.Select(r => ParticipantBlock(r.Run, r.Output))), null, null, null);
            }
            case AgentTeamMode.Handoff:
            {
                var router = await RunAuxiliaryAsync(context, scriptContext, transitionId, binding, "$router", binding.Agent!,
                    basePrompt + AgentDecisionSupport.DescribeParticipants(participants),
                    s => AgentDecisionSupport.AugmentHandoff(s, participants), entryNo, exclude, callerRoles, cancellationToken);
                finished.Add(router);

                var chosen = AgentDecisionSupport.ReadString(router.Output, AgentDecisionSupport.HandoffProperty);
                var target = participants.FirstOrDefault(p => p.Name == chosen);
                if (router.Run.Status != AgentRunStatus.Completed || target?.Agent is null)
                {
                    return await CompleteAsync(string.Empty, null,
                        $"handoff router did not name a participant ({router.Run.FailureReason ?? $"returned '{chosen ?? "nothing"}'"})", chosen);
                }

                var resolved = await ResolveAgentAsync(target.Agent, cancellationToken);
                if (!resolved.IsSuccess)
                    return await CompleteAsync(string.Empty, null, resolved.Error.Message, chosen);

                return await CompleteAsync(ParticipantBlock(router.Run, router.Output), resolved.Value, null, chosen);
            }
            default:
                return await CompleteAsync(string.Empty, lead, null, null);
        }
    }

    /// <summary>
    /// One participant (or handoff router) run: its own component, tools and policy narrowed by the state;
    /// no session, no transcript, no approval (a held call fails the participant). Not added to the journal.
    /// </summary>
    private async Task<(InstanceAgentRun Run, IReadOnlyList<InstanceAgentToolCall> Calls, JsonElement? Output)> RunAuxiliaryAsync(
        TransitionExecutionContext context, ScriptContext scriptContext, Guid? transitionId, AgentStateBinding binding,
        string name, Reference reference, string prompt, Func<JsonElement, JsonElement>? augment, int entryNo,
        IReadOnlySet<string> exclude, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var startedAt = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var state = context.Target!.Key;
        var row = new InstanceAgentRun(Guid.NewGuid())
        {
            InstanceId = context.Instance.Id,
            TransitionId = transitionId,
            State = state,
            EntryNo = entryNo,
            Participant = Truncate(name, 64),
            ExecutionKey = InstanceAgentRun.CreateExecutionKey(transitionId, state, entryNo, name),
            AgentKey = reference.Key,
            AgentDomain = reference.Domain,
            AgentVersion = reference.Version,
            RuntimeKey = AgentRuntimeKeys.Maf,
            ProviderKey = string.Empty,
            StartedAt = startedAt,
            CreatedAt = startedAt
        };

        IReadOnlyList<InstanceAgentToolCall> Finish(AgentRunStatus status, string? failure, IReadOnlyList<InstanceAgentToolCall> calls)
        {
            row.Status = status;
            row.FailureReason = failure is null ? null : Truncate(failure, 400);
            row.FinishedAt = DateTime.UtcNow;
            row.DurationMs = clock.ElapsedMilliseconds;
            return calls;
        }

        var resolved = await ResolveAgentAsync(reference, cancellationToken);
        if (!resolved.IsSuccess)
            return (row, Finish(AgentRunStatus.Failed, resolved.Error.Message, []), null);
        var agent = resolved.Value!;
        row.AgentKey = agent.Key;
        row.AgentDomain = agent.Domain;
        row.AgentVersion = agent.Version;
        row.ProviderKey = agent.Provider;
        if (!settings.Providers.TryGetValue(agent.Provider, out var profile))
            return (row, Finish(AgentRunStatus.Failed, $"provider profile '{agent.Provider}' is not configured", []), null);
        row.Model = profile.Model;

        var policy = EffectiveAgentPolicy.Resolve(agent.Policy, binding.Policy, settings);
        var recorder = new AgentRunRecorder(row.Id, context.Instance.Id, policy, agent.Audit?.PersistToolResults ?? true, startedAt, logger);
        var tools = await toolCatalog.BuildAsync(new AgentToolBuildContext(
            agent, exclude, callerRoles, scriptContext, transitionId, recorder, Stub: false), cancellationToken);
        var schema = augment is null ? agent.Output!.Schema!.Value : augment(agent.Output!.Schema!.Value);
        var instructions = agent.Instructions!.Resolved;

        AgentRunResult result;
        using (var slot = limiter.TryAcquire())
        {
            result = slot is null
                ? AgentRunResult.Failed(AgentRunStatus.Failed, $"capacity: AiAgent:MaxConcurrentRuns ({settings.MaxConcurrentRuns}) reached")
                : await runtime.RunAsync(new AgentRunRequest(
                    row.Id, agent.Key, agent.Provider, profile, instructions, prompt, schema, agent.Model, tools, policy,
                    null, null, false, 0, CaptureTranscript: false), cancellationToken);
        }

        ApplyResult(row, result, recorder, agent.Audit, settings, schema, [], prompt, AgentTemplateRenderer.Sha256(instructions), tools);
        if (result.Status == AgentRunStatus.ApprovalRequested)
            return (row, Finish(AgentRunStatus.Failed, "tool approval is only supported on the final agent of a state", recorder.Calls), null);

        logger.AgentRunCompleted(row.Id, row.InstanceId, clock.ElapsedMilliseconds, row.Iterations, row.ToolCallCount, row.TotalTokens);
        return (row, Finish(result.Status, result.FailureReason, recorder.Calls), result.Status == AgentRunStatus.Completed ? result.Output : null);
    }

    private static string ParticipantBlock(InstanceAgentRun run, JsonElement? output)
    {
        var body = output is { } o
            ? o.GetRawText()
            : JsonSerializer.Serialize(new { error = run.FailureReason ?? run.Status.ToString() });
        return $"\n\n<data name=\"participant:{run.Participant}\">\n{body.Replace("</data", "<\\/data", StringComparison.OrdinalIgnoreCase)}\n</data>";
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
