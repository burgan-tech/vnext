using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Services;
using BBT.Aether.Uow;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions;
using Microsoft.EntityFrameworkCore;
using WorkflowTaskStatus = BBT.Workflow.Definitions.TaskStatus;

namespace BBT.Workflow.Instances;

/// <summary>
/// EF Core implementation of IInstanceTaskRepository.
/// </summary>
public class EfCoreInstanceTaskRepository(
    IAetherDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider,
    IDataSinkManager dataSinkManager)
    : EfCoreRepository<WorkflowDbContext, InstanceTask, Guid>(dbContext, serviceProvider),
        IInstanceTaskRepository
{
    /// <inheritdoc />
    public async Task<InstanceTask?> FindByTransitionAndTaskAsync(
        Guid transitionId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var executionKey = InstanceTask.CreateExecutionKey(transitionId, taskId);

        // ExecutionKey is the deterministic hash of (TransitionId, TaskId), so any row matching
        // the pair either carries exactly this key or a NULL key (row created before the
        // ExecutionKey migration). Filtering on the key lets the planner resolve the common case
        // through UX_InstanceTasks_ExecutionKey as a point lookup; the OR arm keeps legacy rows
        // reachable via the (TransitionId, ...) prefix of the covering index.
        return await dbSet
            .Where(task => task.ExecutionKey == executionKey ||
                           (task.TransitionId == transitionId &&
                            task.TaskId == taskId &&
                            task.ExecutionKey == null))
            .OrderByDescending(task => task.ExecutionKey == executionKey)
            .ThenByDescending(task => task.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts a new instance task and transfers to data sinks
    /// </summary>
    public override async Task<InstanceTask> InsertAsync(InstanceTask entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var result = await base.InsertAsync(entity, autoSave, cancellationToken);
        
        // Transfer to registered data sinks if any
        try
        {
            await dataSinkManager.HandleInsertAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log error but don't fail the main operation
            Console.WriteLine($"Failed to transfer instance task to data sinks: {ex.Message}");
        }
        
        return result;
    }

    /// <summary>
    /// Updates an instance task and transfers to data sinks
    /// </summary>
    public override async Task<InstanceTask> UpdateAsync(InstanceTask entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var result = await base.UpdateAsync(entity, autoSave, cancellationToken);
        
        // Transfer to registered data sinks if any
        try
        {
            await dataSinkManager.HandleUpdateAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log error but don't fail the main operation
            Console.WriteLine($"Failed to transfer instance task to data sinks: {ex.Message}");
        }
        
        return result;
    }

    /// <inheritdoc />
    public async Task<List<InstanceTask>> GetByTransitionIdAsync(
        Guid transitionId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .AsNoTracking()
            .Where(t => t.TransitionId == transitionId)
            .OrderBy(t => t.StartedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
    {
        await (await GetDbSetAsync())
            .Where(t => t.Id == instanceTask.Id)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.Status, instanceTask.Status)
                    .SetProperty(t => t.BusinessStatus, instanceTask.BusinessStatus)
                    .SetProperty(t => t.Response, instanceTask.Response)
                    .SetProperty(t => t.Request, instanceTask.Request)
                    .SetProperty(t => t.InvocationResult, instanceTask.InvocationResult)
                    .SetProperty(t => t.FinishedAt, instanceTask.FinishedAt)
                    .SetProperty(t => t.Duration, instanceTask.Duration),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<InstanceTask>> GetByTransitionIdsAsync(
        IReadOnlyCollection<Guid> transitionIds,
        CancellationToken cancellationToken = default)
    {
        if (transitionIds.Count == 0)
        {
            return [];
        }

        var dbSet = await GetDbSetAsync();
        return await dbSet
            .AsNoTracking()
            .Where(t => transitionIds.Contains(t.TransitionId))
            .OrderBy(t => t.StartedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstanceTask?> GetByIdAsReadOnlyAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetCompletedTaskIdsAsync(
        Guid transitionId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(t => t.TransitionId == transitionId && t.Status == WorkflowTaskStatus.Completed)
            .Select(t => t.TaskId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetTaskIdsByStatusAsync(
        Guid transitionId,
        Definitions.TaskStatus status,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(t => t.TransitionId == transitionId && t.Status == status)
            .Select(t => t.TaskId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetSuccessfulTaskIdsAsync(
        Guid transitionId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(t => t.TransitionId == transitionId &&
                        t.Status == WorkflowTaskStatus.Completed &&
                        t.BusinessStatus == BusinessStatus.Success)
            .Select(t => t.TaskId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<TaskExecutionStat>> GetTaskStatsAsync(
        DateTime? since = null,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var query = dbSet.AsNoTracking();

        // Bounds the aggregation's scan; served by IX_InstanceTasks_StartedAt_Brin (rows are
        // inserted in StartedAt order, so the BRIN range map stays tiny).
        if (since is { } lowerBound)
        {
            query = query.Where(t => t.StartedAt >= lowerBound);
        }

        var counts = await query
            .GroupBy(t => t.TaskId)
            .Select(g => new
            {
                TaskKey = g.Key,
                ExecutionCount = g.Count(),
                SuccessCount = g.Count(x => x.BusinessStatus == BusinessStatus.Success),
                FailureCount = g.Count(x => x.BusinessStatus == BusinessStatus.Failed)
            })
            .ToListAsync(cancellationToken);

        return counts.Select(c => new TaskExecutionStat(
            c.TaskKey,
            c.ExecutionCount,
            c.SuccessCount,
            c.FailureCount)).ToList();
    }

    /// <inheritdoc />
    public async Task<List<InstanceTaskRow>> GetByInstanceIdAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        var rows = await (
            from task in context.InstanceTasks.AsNoTracking()
            join tr in context.InstanceTransitions.AsNoTracking()
                on task.TransitionId equals tr.Id
            where tr.InstanceId == instanceId
            orderby task.StartedAt
            select new
            {
                Task = task,
                TransitionKey = tr.TransitionId,
                tr.FromState,
                tr.ToState,
                tr.TriggerType
            }
        ).ToListAsync(cancellationToken);

        return rows.Select(r => new InstanceTaskRow(
            r.Task,
            r.TransitionKey,
            r.FromState,
            r.ToState,
            r.TriggerType
        )).ToList();
    }
}
