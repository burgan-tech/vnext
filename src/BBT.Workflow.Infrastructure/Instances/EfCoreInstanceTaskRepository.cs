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
    IDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider,
    IDataSinkManager dataSinkManager)
    : EfCoreRepository<WorkflowDbContext, InstanceTask, Guid>(dbContext, serviceProvider),
        IInstanceTaskRepository
{
    /// <summary>
    /// Inserts a new instance task and transfers to data sinks
    /// </summary>
    public override async Task<InstanceTask> InsertAsync(InstanceTask entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var result = await base.InsertAsync(entity, autoSave, cancellationToken);
        
        // Transfer to data sinks (e.g., ClickHouse) if enabled
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
        
        // Transfer to data sinks (e.g., ClickHouse) if enabled
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
            .Where(t => t.TransitionId == transitionId)
            .OrderBy(t => t.StartedAt)
            .ToListAsync(cancellationToken);
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
}
