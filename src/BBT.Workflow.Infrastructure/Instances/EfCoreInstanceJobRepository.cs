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

    /// <inheritdoc />
    public async Task<HashSet<Guid>> GetInstanceIdsWithActiveJobAsync(
        IEnumerable<Guid> instanceIds, CancellationToken cancellationToken = default)
    {
        var ids = instanceIds.ToList();
        if (ids.Count == 0)
            return [];

        var result = await (await GetQueryableAsync())
            .Where(j => ids.Contains(j.InstanceId) && j.IsActive == true)
            .Select(j => j.InstanceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. result];
    }
}