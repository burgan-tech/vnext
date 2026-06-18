using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Instances;

public sealed class EfCoreInstanceJobRepository(
    IDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider)
    : EfCoreRepository<WorkflowDbContext, InstanceJob, Guid>(dbContext, serviceProvider),
        IInstanceJobRepository
{
    public async Task<List<InstanceJob>> GetListActiveAsync(Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        return await (await GetQueryableAsync())
            .Where(p => p.InstanceId == instanceId && p.IsActive == true)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<InstanceJob>> GetActiveForStateCancellationAsync(
        Guid instanceId,
        IReadOnlyCollection<JobType> matchTypes,
        IReadOnlyCollection<string> transitionKeys,
        CancellationToken cancellationToken = default)
    {
        return await (await GetQueryableAsync())
            .Where(j => j.InstanceId == instanceId && j.IsActive == true
                && ((matchTypes.Contains(j.JobType)
                        && j.TransitionKey != null
                        && transitionKeys.Contains(j.TransitionKey))
                    // Transitional fallback: legacy rows carry no structured columns; the service
                    // matches them by the old "-{key}" suffix until none remain.
                    || j.JobType == JobType.Unknown))
            .ToListAsync(cancellationToken);
    }

    public async Task MarkAsProcessedAsync(Guid instanceId, string jobName,
        CancellationToken cancellationToken = default)
    {
        var job = await (await GetDbSetAsync()).FirstOrDefaultAsync(p =>
            p.InstanceId == instanceId &&
            p.JobName == jobName && p.IsActive == true, cancellationToken);
        if (job != null)
        {
            job.MarkAsProcessed();
            await SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.JobId == jobId, cancellationToken);
    }

    public async Task<bool> AnyActiveByJobNameAsync(Guid instanceId, string jobName,
        CancellationToken cancellationToken = default)
        => await (await GetQueryableAsync())
            .AnyAsync(j => j.InstanceId == instanceId && j.JobName == jobName && j.IsActive == true, cancellationToken);
}