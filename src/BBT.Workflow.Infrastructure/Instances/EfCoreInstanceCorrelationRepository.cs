using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Uow;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Instances;

/// <summary>
/// Entity Framework Core implementation of the IInstanceCorrelationRepository interface.
/// This repository provides data access operations for managing workflow instance correlations
/// using Entity Framework Core and PostgreSQL.
/// </summary>
/// <param name="dbContext">The workflow database context for data operations.</param>
/// <param name="serviceProvider">Service provider for dependency injection.</param>
public sealed class EfCoreInstanceCorrelationRepository(
    IDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider)
    : EfCoreRepository<WorkflowDbContext, InstanceCorrelation, Guid>(dbContext, serviceProvider),
        IInstanceCorrelationRepository
{
    /// <inheritdoc />
    public async Task<List<InstanceCorrelation>> GetByParentAsync(
        Guid parentInstanceId,
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .Where(c => c.ParentInstanceId == parentInstanceId)
            .OrderBy(c => c.ParentState)
            .ThenBy(c => c.CompletedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<InstanceCorrelation>> GetActiveByParentAsync(
        Guid parentInstanceId,
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .Where(c => c.ParentInstanceId == parentInstanceId && !c.IsCompleted)
            .ToListAsync(cancellationToken);
    }

     /// <inheritdoc />
    public async Task<bool> AnyActiveSubFlowByParentAsync(Guid parentInstanceId, CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .Where(c =>
                c.ParentInstanceId == parentInstanceId
                && !c.IsCompleted
                && c.SubFlowType == SubFlowType.SubFlow)
            .AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstanceCorrelation?> FindActiveSubFlowByParentAsync(Guid parentInstanceId, CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .FirstOrDefaultAsync(c =>
                    c.ParentInstanceId == parentInstanceId
                    && !c.IsCompleted
                    && c.SubFlowType == SubFlowType.SubFlow,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstanceCorrelation?> FindBySubInstanceIdAsync(
        Guid subInstanceId, 
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .FirstOrDefaultAsync(c => c.SubFlowInstanceId == subInstanceId, cancellationToken);
    }
}