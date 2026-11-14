using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Agents.Tools;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Agents;

/// <summary>Dry-run request.</summary>
public sealed class AgentDryRunInput
{
    /// <summary>Domain.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Agent key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Agent version; latest when empty.</summary>
    public string? Version { get; set; }

    /// <summary>The user prompt; may use <c>{{ data.* }}</c> over <see cref="Data"/>.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Sample instance data for the prompt.</summary>
    public JsonElement? Data { get; set; }

    /// <summary>Candidates to choose from (model decision); empty runs without a decision.</summary>
    public List<AgentCandidate>? Candidates { get; set; }

    /// <summary>Caller headers (roles).</summary>
    public IReadOnlyDictionary<string, string?>? Headers { get; set; }
}

/// <summary>
/// Dry-run result. <see cref="Status"/> <c>ApprovalRequested</c> is not a failure: the agent asked for a tool
/// that needs approval and the run stopped there (a dry run has no session to resume). The held calls are in
/// <see cref="PendingApprovals"/> and <see cref="FailureReason"/> is empty.
/// </summary>
public sealed record AgentDryRunOutput(
    string Status,
    JsonElement? Output,
    string? Decision,
    string? Reason,
    string? FailureReason,
    IReadOnlyList<AgentToolCallItem> ToolCalls,
    int PromptTokens,
    int CompletionTokens,
    long DurationMs)
{
    /// <summary>Tool calls held for approval; empty unless <see cref="Status"/> is <c>ApprovalRequested</c>.</summary>
    public IReadOnlyList<AgentDryRunPendingApproval> PendingApprovals { get; init; } = [];
}

/// <summary>A tool call the dry run held for approval (same names as <c>$agent.pendingApprovals</c>).</summary>
/// <param name="RequestId">Approval request id.</param>
/// <param name="CallId">Model tool-call id.</param>
/// <param name="Tool">Tool name.</param>
/// <param name="Arguments">Arguments the model chose.</param>
/// <param name="RequiresConfirmation">True when the tool is marked <c>requiresApproval</c>.</param>
public sealed record AgentDryRunPendingApproval(
    string RequestId,
    string CallId,
    string Tool,
    JsonElement Arguments,
    bool RequiresConfirmation);

/// <summary>Dry-runs an agent component (issue #1105).</summary>
public interface IAgentDryRunAppService
{
    /// <summary>Runs the agent once without an instance.</summary>
    Task<Result<AgentDryRunOutput>> RunAsync(AgentDryRunInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs an agent once against sample data without an instance: no journal, no session, no data write,
/// and every tool answers a stub (tools that run tasks need an instance context, and a dry run must not
/// cause side effects). Allowed only for the roles in <c>AiAgent:DryRun:Roles</c>; empty disables it.
/// </summary>
public sealed class AgentDryRunAppService(
    IRuntimeInfoProvider runtimeInfoProvider,
    IComponentCacheStore componentCacheStore,
    ICurrentSchema currentSchema,
    IOptions<AiAgentOptions> options,
    IAgentRuntime runtime,
    AgentToolCatalog toolCatalog,
    ICallerRoleResolver callerRoleResolver,
    IScriptContextFactory scriptContextFactory,
    BBT.Workflow.Instances.IInstanceRepository instanceRepository,
    ILogger<AgentDryRunAppService> logger) : IAgentDryRunAppService
{
    /// <inheritdoc />
    public async Task<Result<AgentDryRunOutput>> RunAsync(AgentDryRunInput input, CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);
        var settings = options.Value;

        var roles = await callerRoleResolver.ResolveRolesAsync(input.Headers, cancellationToken);
        var callerRoles = roles.IsSuccess ? roles.Value ?? [] : [];
        if (settings.DryRun.Roles.Count == 0 || callerRoles.All(r => !settings.DryRun.Roles.Contains(r, StringComparer.OrdinalIgnoreCase)))
            return Result<AgentDryRunOutput>.Fail(Error.Forbidden(WorkflowErrorCodes.AgentDryRunForbidden, "Agent dry-run is not allowed for this caller."));

        var agentResult = await GetAgentAsync(input, cancellationToken);
        if (!agentResult.IsSuccess)
            return Result<AgentDryRunOutput>.Fail(agentResult.Error);
        var agent = agentResult.Value!;

        var errors = new List<ValidationResult>();
        AgentComponentValidator.Validate(agent, errors, settings);
        if (errors.Count > 0)
            return Result<AgentDryRunOutput>.Fail(Error.Validation(WorkflowErrorCodes.AgentDryRunInvalid, string.Join(" ", errors.Select(e => e.ErrorMessage))));
        if (!settings.Providers.TryGetValue(agent.Provider, out var profile))
            return Result<AgentDryRunOutput>.Fail(Error.Failure(WorkflowErrorCodes.AgentProviderNotConfigured, $"Provider profile '{agent.Provider}' is not configured."));

        var candidates = input.Candidates ?? [];
        var mode = candidates.Count > 0 ? AgentDecisionMode.Model : AgentDecisionMode.Rules;
        var prompt = AgentTemplateRenderer.Render(input.Prompt, new AgentTemplateRenderer.Scope(input.Data, null, Guid.Empty, null, "dry-run"));
        if (mode == AgentDecisionMode.Model)
            prompt += AgentDecisionSupport.DescribeCandidates(candidates);

        var policy = EffectiveAgentPolicy.Resolve(agent.Policy, null, settings);
        var runId = Guid.NewGuid();
        var clock = Stopwatch.StartNew();
        await using var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository).BuildAsync(cancellationToken);
        var recorder = new AgentRunRecorder(runId, Guid.Empty, policy, true, DateTime.UtcNow, logger);
        var tools = await toolCatalog.BuildAsync(new AgentToolBuildContext(
            agent, new HashSet<string>(), callerRoles, scriptContext, null, recorder, Stub: true), cancellationToken);

        var result = await runtime.RunAsync(new AgentRunRequest(
            runId, agent.Key, agent.Provider, profile, agent.Instructions!.Resolved, prompt,
            AgentDecisionSupport.AugmentSchema(agent.Output!.Schema!.Value, mode, candidates),
            agent.Model, tools, policy, null, null, false, 0, false), cancellationToken);

        var chosen = AgentDecisionSupport.ReadString(result.Output, AgentDecisionSupport.DecisionProperty);
        return Result<AgentDryRunOutput>.Ok(new AgentDryRunOutput(
            result.Status.ToString(),
            result.Output,
            candidates.Any(c => c.Key == chosen) ? chosen : null,
            AgentDecisionSupport.ReadString(result.Output, AgentDecisionSupport.ReasonProperty),
            FailureReasonOf(result),
            recorder.Calls.Select(c => new AgentToolCallItem(c.Id, c.Sequence, c.Iteration, c.CallId, c.Name, (short)c.Kind,
                c.BindingRef, c.Status, c.Error, null, c.StartedAt, c.DurationMs)).ToList(),
            result.PromptTokens,
            result.CompletionTokens,
            clock.ElapsedMilliseconds)
        {
            PendingApprovals = PendingApprovalsOf(result)
        });
    }

    /// <summary>The failure text; empty for an approval stop, which is an outcome rather than a failure.</summary>
    internal static string? FailureReasonOf(AgentRunResult result) =>
        result.Status == AgentRunStatus.ApprovalRequested ? null : result.FailureReason;

    /// <summary>The held tool calls of an approval stop.</summary>
    internal static IReadOnlyList<AgentDryRunPendingApproval> PendingApprovalsOf(AgentRunResult result) =>
        result.Status != AgentRunStatus.ApprovalRequested
            ? []
            : result.PendingApprovals
                .Select(a => new AgentDryRunPendingApproval(a.RequestId, a.CallId, a.ToolName, a.Arguments, a.RequiresConfirmation))
                .ToList();

    /// <summary>Loads the agent component from the <c>sys-agents</c> schema.</summary>
    private async Task<Result<Agent>> GetAgentAsync(AgentDryRunInput input, CancellationToken cancellationToken)
    {
        using (currentSchema.Change(RuntimeSysSchemaInfo.Agents))
            return await componentCacheStore.GetAgentAsync(input.Domain, input.Key, input.Version, cancellationToken);
    }
}
