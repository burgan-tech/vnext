using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Instances;

public sealed class EfCoreInstanceJobRepository(
    IAetherDbContextProvider<WorkflowDbContext> dbContext,
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

    public async Task MarkAsProcessedAsync(Guid instanceId, string jobName,
        CancellationToken cancellationToken = default)
    {
        // Single set-based statement instead of SELECT + tracked mutate + SaveChanges: this runs
        // in every job handler's finally, so it used to cost two round-trips per fired job. The
        // SetProperty pair mirrors InstanceJob.MarkAsProcessed() exactly (IsActive=false,
        // ModifiedAt=UtcNow — the entity mutates nothing else); ExecuteUpdate bypassing the change
        // tracker is therefore behavior-identical. The WHERE matches the partial index
        // IX_InstanceJobs_Active_Instance_JobName (filtered on IsActive = true).
        await (await GetDbSetAsync())
            .Where(p => p.InstanceId == instanceId && p.JobName == jobName && p.IsActive == true)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.IsActive, false)
                    .SetProperty(j => j.ModifiedAt, DateTime.UtcNow),
                cancellationToken);
    }

    public async Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.JobId == jobId, cancellationToken);
    }

    public async Task<bool> AnyActiveTransitionJobAsync(
        Guid instanceId,
        JobType jobType,
        string? sourceState,
        string transitionKey,
        CancellationToken cancellationToken = default)
        => await (await GetQueryableAsync())
            .AnyAsync(
                j => j.InstanceId == instanceId
                     && j.IsActive == true
                     && j.JobType == jobType
                     && j.SourceState == sourceState
                     && j.TransitionKey == transitionKey,
                cancellationToken);

    /// <inheritdoc />
    public async Task<List<InstanceJob>> GetActiveByFlowAsync(
        string flow,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default)
    {
        return await (await GetQueryableAsync())
            .AsNoTracking()
            .Where(j => j.IsActive == true && j.FlowName == flow)
            .Where(j => createdAtGte == null || j.CreatedAt >= createdAtGte)
            .Where(j => createdAtLte == null || j.CreatedAt <= createdAtLte)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<InstanceJob>> GetActiveByFlowPagedAsync(
        string flow,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await (await GetQueryableAsync())
            .AsNoTracking()
            .Where(j => j.IsActive == true && j.FlowName == flow)
            .Where(j => createdAtGte == null || j.CreatedAt >= createdAtGte)
            .Where(j => createdAtLte == null || j.CreatedAt <= createdAtLte)
            .OrderByDescending(j => j.CreatedAt)
            .Skip(skip)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<InstanceJob>> GetActiveByDomainAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        return await (await GetQueryableAsync())
            .AsNoTracking()
            .Where(j => j.IsActive == true && j.Domain == domain)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

}