using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Agents;

/// <summary>Input of the <c>agent-runs</c> system function.</summary>
public sealed class GetAgentRunsInput : IHasDomain
{
    /// <summary>Domain.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Workflow.</summary>
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance id or key.</summary>
    public string Instance { get; set; } = string.Empty;

    /// <summary>Optional state filter.</summary>
    public string? State { get; set; }

    /// <summary>Optional run id: returns that run with its tool calls.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Page offset.</summary>
    public int Skip { get; set; }

    /// <summary>Page size (1..100).</summary>
    public int Take { get; set; } = 20;
}

/// <summary>A run and its tool calls.</summary>
/// <param name="Run">The run.</param>
/// <param name="ToolCalls">Tool calls in order.</param>
public sealed record AgentRunDetailDto(AgentRunListItem Run, IReadOnlyList<AgentToolCallItem> ToolCalls);

/// <summary>Output of the <c>agent-runs</c> system function.</summary>
/// <param name="Items">Runs, newest first (listing).</param>
/// <param name="Detail">One run with its tool calls (<c>?runId=</c>).</param>
public sealed record GetAgentRunsOutput(IReadOnlyList<AgentRunListItem>? Items, AgentRunDetailDto? Detail);

/// <summary>Read surface of the agent journal (issue #1105).</summary>
public interface IAgentRunQueryAppService
{
    /// <summary>Lists the instance's agent runs, or one run with its tool calls.</summary>
    Task<Result<GetAgentRunsOutput>> GetAsync(GetAgentRunsInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// The <c>agent-runs</c> system function: metadata only — prompts, outputs, tool arguments and results,
/// transcripts and sessions never leave the journal through this surface. A GUID instance identifier
/// costs no instance load; a business key costs one read-only lookup. One query for the listing (decision
/// joined), two for a run detail.
/// </summary>
public sealed class AgentRunQueryAppService(
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceRepository instanceRepository,
    IAgentRunReadRepository readRepository) : IAgentRunQueryAppService
{
    /// <inheritdoc />
    public async Task<Result<GetAgentRunsOutput>> GetAsync(GetAgentRunsInput input, CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);
        using var read = InstanceReadActivityHelper.StartRead(InstanceReadKinds.AgentRuns, input.Domain, input.Workflow); // skipcq: CS-W1100 — held for its scope; disposing ends the span

        Guid instanceId;
        if (!Guid.TryParse(input.Instance, out instanceId))
        {
            var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(input.Instance, cancellationToken);
            if (instance is null)
                return Result<GetAgentRunsOutput>.Fail(WorkflowErrors.InstanceNotFound(input.Instance));
            instanceId = instance.Id;
        }

        if (input.RunId is { } runId)
        {
            var run = await readRepository.FindAsync(instanceId, runId, cancellationToken);
            if (run is null)
            {
                return Result<GetAgentRunsOutput>.Fail(Error.NotFound(
                    WorkflowErrorCodes.AgentRunNotFound, $"Agent run '{runId}' was not found for instance '{input.Instance}'.", target: "runId"));
            }

            var calls = await readRepository.ListToolCallsAsync(runId, cancellationToken);
            return Result<GetAgentRunsOutput>.Ok(new GetAgentRunsOutput(null, new AgentRunDetailDto(run, calls)));
        }

        var items = await readRepository.ListAsync(instanceId, input.State, Math.Max(0, input.Skip), Math.Clamp(input.Take, 1, 100), cancellationToken);
        return Result<GetAgentRunsOutput>.Ok(new GetAgentRunsOutput(items, null));
    }
}
