using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Instances.Agents;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Agents;

/// <summary>
/// Read side of the agent journal (issue #1105): narrow projections only — the request, response and
/// transcript columns are never selected, so their TOASTed values are never read. The listing joins the
/// decision in the same statement.
/// </summary>
public sealed class EfCoreAgentRunReadRepository(IAetherDbContextProvider<WorkflowDbContext> dbContextProvider) : IAgentRunReadRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentRunListItem>> ListAsync(Guid instanceId, string? state, int skip, int take, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var runs = context.InstanceAgentRuns.AsNoTracking().Where(r => r.InstanceId == instanceId);
        if (!string.IsNullOrEmpty(state))
            runs = runs.Where(r => r.State == state);

        return await Project(runs.OrderByDescending(r => r.StartedAt).Skip(skip).Take(take), context)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AgentRunListItem?> FindAsync(Guid instanceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        return await Project(context.InstanceAgentRuns.AsNoTracking().Where(r => r.Id == runId && r.InstanceId == instanceId), context)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<byte[]?> FindTranscriptAsync(Guid instanceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        return await context.InstanceAgentRuns
            .AsNoTracking()
            .Where(r => r.Id == runId && r.InstanceId == instanceId)
            .Select(r => r.Transcript)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentToolCallItem>> ListToolCallsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        return await context.InstanceAgentToolCalls
            .AsNoTracking()
            .Where(c => c.RunId == runId)
            .OrderBy(c => c.Sequence)
            .Select(c => new AgentToolCallItem(
                c.Id, c.Sequence, c.Iteration, c.CallId, c.Name, (short)c.Kind, c.BindingRef, c.Status, c.Error,
                c.InstanceTaskId, c.StartedAt, c.DurationMs))
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<AgentRunListItem> Project(IQueryable<InstanceAgentRun> runs, WorkflowDbContext context) =>
        from r in runs
        join d in context.InstanceAgentDecisions.AsNoTracking() on r.Id equals d.RunId into decisions
        from d in decisions.DefaultIfEmpty()
        select new AgentRunListItem(
            r.Id, r.TransitionId, r.State, r.EntryNo, r.AgentKey, r.AgentVersion, r.ProviderKey, r.Model, r.Status,
            r.FailureReason, r.Attempts, r.StartedAt, r.FinishedAt, r.DurationMs, r.Iterations, r.ToolCallCount,
            r.TotalTokens, r.SessionScopeKey, r.SessionResumed,
            d == null ? null : d.SelectedTransition,
            d == null ? null : (AgentDecisionSource?)d.Source,
            d == null ? null : d.Reason,
            r.Participant);
}
