using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Instances;

/// <summary>EF Core read-only implementation of <see cref="IInstanceActionRepository"/>.</summary>
public sealed class EfCoreInstanceActionRepository(
    IAetherDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider)
    : EfCoreRepository<WorkflowDbContext, InstanceAction, Guid>(dbContext, serviceProvider),
        IInstanceActionRepository
{
    /// <inheritdoc />
    public async Task<List<InstanceAction>> GetByTaskIdAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.StartedAt)
            .ToListAsync(cancellationToken);
    }
}
