using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Services;
using BBT.Aether.Uow;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Instances;

public class EfCoreInstanceTransitionRepository(
    IDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider,
    IDataSinkManager dataSinkManager)
    : EfCoreRepository<WorkflowDbContext, InstanceTransition, Guid>(dbContext, serviceProvider),
        IInstanceTransitionRepository
{
    /// <summary>
    /// Inserts a new instance transition and transfers to data sinks
    /// </summary>
    public override async Task<InstanceTransition> InsertAsync(InstanceTransition entity, bool autoSave = false, CancellationToken cancellationToken = default)
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
            Console.WriteLine($"Failed to transfer instance transition to data sinks: {ex.Message}");
        }
        
        return result;
    }

    /// <summary>
    /// Updates an instance transition and transfers to data sinks
    /// </summary>
    public override async Task<InstanceTransition> UpdateAsync(InstanceTransition entity, bool autoSave = false, CancellationToken cancellationToken = default)
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
            Console.WriteLine($"Failed to transfer instance transition to data sinks: {ex.Message}");
        }
        
        return result;
    }

    public async Task UpdateCompletedAsync(InstanceTransition transition, CancellationToken cancellationToken)
    {
        var context = await GetDbContextAsync();
        await context.InstanceTransitions
            .Where(p => p.Id == transition.Id)
            .ExecuteUpdateAsync(sp => sp
                    .SetProperty(p => p.ToState, transition.ToState)
                    .SetProperty(p => p.FinishedAt, transition.FinishedAt)
                    .SetProperty(p => p.Duration, transition.Duration),
                cancellationToken
            );
    }

    /// <inheritdoc />
    public async Task<InstanceTransition?> GetLatestIncompleteAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        return await context.InstanceTransitions
            .Where(p => p.InstanceId == instanceId && p.FinishedAt == null)
            .OrderByDescending(p => p.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstanceTransition?> GetLastCompletedManualTransitionAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        return await context.InstanceTransitions
            .Where(p => p.InstanceId == instanceId && p.FinishedAt != null && p.TriggerType == TriggerType.Manual)
            .OrderByDescending(p => p.FinishedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}