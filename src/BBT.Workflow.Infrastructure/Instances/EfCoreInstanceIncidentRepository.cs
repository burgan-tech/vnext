using BBT.Aether;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Uow;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Instances;

/// <summary>
/// Entity Framework Core implementation of <see cref="IInstanceIncidentRepository"/>.
/// Read-side access to the <c>InstanceIncidents</c> table independent of the instance aggregate:
/// the paged history and the newest unresolved incident.
/// Every read is no-tracking; writes go through the aggregate.
/// </summary>
/// <param name="dbContext">The workflow database context provider.</param>
/// <param name="serviceProvider">Service provider for dependency injection.</param>
public sealed class EfCoreInstanceIncidentRepository(
    IAetherDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider)
    : EfCoreRepository<WorkflowDbContext, InstanceIncident, Guid>(dbContext, serviceProvider),
        IInstanceIncidentRepository
{
    /// <inheritdoc />
    public async Task<HateoasPagedList<InstanceIncident>> GetHistoryPagedAsync(
        Guid instanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;

        // N+1 read: one extra row tells us whether a next page exists without a COUNT(*).
        var rows = await (await GetDbSetAsync())
            .AsNoTracking()
            .Where(i => i.InstanceId == instanceId)
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasNext = rows.Count > pageSize;
        if (hasNext)
            rows.RemoveAt(rows.Count - 1);

        return new HateoasPagedList<InstanceIncident>(rows, page, pageSize, hasNext);
    }

    /// <inheritdoc />
    public async Task<InstanceIncident?> GetActiveAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        // Served by IX_InstanceIncidents_InstanceId_CreatedAt; the ordering matches every other
        // "newest incident" rule in the codebase (CreatedAt DESC, then Id DESC to break a tie
        // between rows recorded in the same unit of work).
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .Where(i => i.InstanceId == instanceId && !i.IsResolved)
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ResolveAllAsync(
        Guid instanceId,
        DateTime resolvedAt,
        CancellationToken cancellationToken = default)
    {
        // Set-based on purpose: the caller's copies of these rows may be tracked by a different
        // context (or by none), so going through the change tracker would either miss the write or
        // make a row look new to whoever else holds the aggregate. InstanceIncident carries no audit
        // columns and raises no domain events, so nothing is lost by bypassing SaveChanges.
        // Served by IX_InstanceIncidents_InstanceId_CreatedAt.
        return await (await GetDbSetAsync())
            .Where(i => i.InstanceId == instanceId && !i.IsResolved)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(i => i.IsResolved, true)
                    .SetProperty(i => i.ResolvedAt, resolvedAt),
                cancellationToken);
    }
}
