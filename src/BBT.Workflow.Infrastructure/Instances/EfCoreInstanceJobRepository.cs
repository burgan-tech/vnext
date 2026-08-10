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

    /// <inheritdoc />
    public async Task<bool> MarkAsProcessedByJobIdAsync(
        Guid jobId,
        Guid processingToken,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var affected = await (await GetDbSetAsync())
            .Where(item => item.JobId == jobId
                           && item.IsActive
                           && item.DispatchStatus == InstanceJobDispatchStatus.Processing
                           && item.ProcessingToken == processingToken)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.IsActive, false)
                    .SetProperty(item => item.DispatchStatus, InstanceJobDispatchStatus.Completed)
                    .SetProperty(item => item.ProcessingLeaseUntil, (DateTime?)null)
                    .SetProperty(item => item.ProcessingToken, (Guid?)null)
                    .SetProperty(item => item.Payload, (string?)null)
                    .SetProperty(item => item.ModifiedAt, now),
                cancellationToken);

        return affected == 1;
    }

    public async Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.JobId == jobId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstanceJob?> FindByIdempotencyKeyAsReadOnlyAsync(
        Guid instanceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .FirstOrDefaultAsync(
                job => job.InstanceId == instanceId && job.IdempotencyKey == idempotencyKey,
                cancellationToken);
    }

    public async Task<bool> AnyActiveByJobNameAsync(Guid instanceId, string jobName,
        CancellationToken cancellationToken = default)
        => await (await GetQueryableAsync())
            .AnyAsync(j => j.InstanceId == instanceId && j.JobName == jobName && j.IsActive == true, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(
        Guid jobId,
        Guid processingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (processingToken == Guid.Empty)
            throw new ArgumentException("Processing token cannot be empty.", nameof(processingToken));

        var now = DateTime.UtcNow;
        var leaseUntil = now.Add(leaseDuration);
        var affected = await (await GetDbSetAsync())
            .Where(job => job.JobId == jobId
                          && job.IsActive
                          && (job.ProcessingLeaseUntil == null || job.ProcessingLeaseUntil < now))
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.DispatchStatus, InstanceJobDispatchStatus.Processing)
                    .SetProperty(job => job.ProcessingAt, now)
                    .SetProperty(job => job.ProcessingLeaseUntil, leaseUntil)
                    .SetProperty(job => job.ProcessingToken, processingToken)
                    .SetProperty(job => job.AttemptCount, job => job.AttemptCount + 1)
                    .SetProperty(job => job.ModifiedAt, now),
                cancellationToken);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> IsClaimOwnerAsync(
        Guid jobId,
        Guid processingToken,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        return await (await GetQueryableAsync())
            .AsNoTracking()
            .AnyAsync(item => item.JobId == jobId
                              && item.IsActive
                              && item.DispatchStatus == InstanceJobDispatchStatus.Processing
                              && item.ProcessingToken == processingToken
                              && item.ProcessingLeaseUntil != null
                              && item.ProcessingLeaseUntil > now,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseClaimAsync(
        Guid jobId,
        Guid processingToken,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var affected = await (await GetDbSetAsync())
            .Where(item => item.JobId == jobId
                           && item.IsActive
                           && item.DispatchStatus == InstanceJobDispatchStatus.Processing
                           && item.ProcessingToken == processingToken)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.DispatchStatus, InstanceJobDispatchStatus.Scheduled)
                    .SetProperty(item => item.ProcessingAt, (DateTime?)null)
                    .SetProperty(item => item.ProcessingLeaseUntil, (DateTime?)null)
                    .SetProperty(item => item.ProcessingToken, (Guid?)null)
                    .SetProperty(item => item.NextAttemptAt, now)
                    .SetProperty(item => item.ModifiedAt, now),
                cancellationToken);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsFailedAsync(
        Guid jobId,
        Guid processingToken,
        string errorCode,
        string? errorDetails = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Length > InstanceJobConstants.MaxErrorCodeLength)
            throw new ArgumentException(
                $"Error code cannot exceed {InstanceJobConstants.MaxErrorCodeLength} characters.",
                nameof(errorCode));

        var now = DateTime.UtcNow;
        var affected = await (await GetDbSetAsync())
            .Where(item => item.JobId == jobId
                           && item.IsActive
                           && item.DispatchStatus == InstanceJobDispatchStatus.Processing
                           && item.ProcessingToken == processingToken)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.IsActive, false)
                    .SetProperty(item => item.DispatchStatus, InstanceJobDispatchStatus.Failed)
                    .SetProperty(item => item.ErrorCode, errorCode)
                    .SetProperty(item => item.ErrorDetails, errorDetails)
                    .SetProperty(item => item.ProcessingLeaseUntil, (DateTime?)null)
                    .SetProperty(item => item.ProcessingToken, (Guid?)null)
                    .SetProperty(item => item.Payload, (string?)null)
                    .SetProperty(item => item.ModifiedAt, now),
                cancellationToken);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsSupersededAsync(
        Guid jobId,
        Guid processingToken,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var affected = await (await GetDbSetAsync())
            .Where(item => item.JobId == jobId
                           && item.IsActive
                           && item.DispatchStatus == InstanceJobDispatchStatus.Processing
                           && item.ProcessingToken == processingToken)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.IsActive, false)
                    .SetProperty(item => item.DispatchStatus, InstanceJobDispatchStatus.Superseded)
                    .SetProperty(item => item.ErrorDetails, reason)
                    .SetProperty(item => item.ProcessingLeaseUntil, (DateTime?)null)
                    .SetProperty(item => item.ProcessingToken, (Guid?)null)
                    .SetProperty(item => item.Payload, (string?)null)
                    .SetProperty(item => item.ModifiedAt, now),
                cancellationToken);

        return affected == 1;
    }

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
        IEnumerable<Guid> instanceIds,
        DateTime pendingDispatchCutoff,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        var ids = instanceIds.ToList();
        if (ids.Count == 0)
            return [];

        var result = await (await GetQueryableAsync())
            .Where(j => ids.Contains(j.InstanceId)
                        && j.IsActive
                        && j.JobType == JobType.AsyncTransition
                        && (((j.DispatchStatus == InstanceJobDispatchStatus.Scheduled
                              || j.DispatchStatus == InstanceJobDispatchStatus.PendingDispatch)
                             && (j.ModifiedAt ?? j.CreatedAt) >= pendingDispatchCutoff)
                            || (j.DispatchStatus == InstanceJobDispatchStatus.Processing
                                && j.ProcessingLeaseUntil != null
                                && j.ProcessingLeaseUntil > utcNow)))
            .Select(j => j.InstanceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. result];
    }
}
