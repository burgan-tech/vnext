using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Instances.Agents;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Agents;

/// <summary>
/// EF Core agent run journal (issue #1105). <see cref="AddAsync"/> only ADDS the rows to the hop's
/// DbContext: they reach the database with the next save or the unit-of-work commit, as one batch,
/// without a separate round trip of their own. The entry count is one index seek on
/// <c>IX_InstanceAgentRuns_Instance_StartedAt</c> filtered by state.
/// </summary>
public sealed class EfCoreAgentRunJournal(IAetherDbContextProvider<WorkflowDbContext> dbContextProvider) : IAgentRunJournal
{
    /// <inheritdoc />
    public async Task<int> CountRunsAsync(Guid instanceId, string state, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        return await context.InstanceAgentRuns
            .AsNoTracking()
            .CountAsync(r => r.InstanceId == instanceId && r.State == state && r.Participant == null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddAsync(
        InstanceAgentRun run,
        IReadOnlyList<InstanceAgentToolCall> toolCalls,
        InstanceAgentDecision? decision,
        CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        context.InstanceAgentRuns.Add(run);
        if (toolCalls.Count > 0)
            context.InstanceAgentToolCalls.AddRange(toolCalls);
        if (decision is not null)
            context.InstanceAgentDecisions.Add(decision);
    }
}
