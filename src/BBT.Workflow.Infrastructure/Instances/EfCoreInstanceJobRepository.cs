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

    public async Task MarkAsProcessedAsync(string jobName, CancellationToken cancellationToken = default)
    {
        var job = await (await GetDbSetAsync()).FirstOrDefaultAsync(p =>
            p.JobName == jobName, cancellationToken);
        job!.MarkAsProcessed();
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.JobId == jobId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<InstanceJob>> GetActiveByFlowAsync(
        string flow,
        CancellationToken cancellationToken = default)
    {
        return await (await GetQueryableAsync())
            .AsNoTracking()
            .Where(j => j.IsActive == true && j.FlowName == flow)
            .OrderByDescending(j => j.CreatedAt)
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