using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Common.DTOs;
using BBT.Workflow.Monitor.Jobs.DTOs;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Monitor.Jobs;

/// <inheritdoc cref="IMonitorJobQueryService" />
public sealed class MonitorJobQueryService(
    IServiceProvider serviceProvider,
    IInstanceJobRepository jobRepository,
    IServiceScopeFactory serviceScopeFactory)
    : ApplicationService(serviceProvider), IMonitorJobQueryService
{
    /// <inheritdoc />
    public async Task<Result<MonitorPagedResponse<MonitorJobItem>>> GetActiveJobsAsync(
        MonitorGetActiveJobsInput input, CancellationToken cancellationToken = default)
    {
        var isDomainWide = string.IsNullOrWhiteSpace(input.Workflow);

        var validation = JobsFilterValidator.Validate(input.Filter, isDomainWide);
        if (validation != JobsFilterValidation.Valid)
            return Result<MonitorPagedResponse<MonitorJobItem>>.Fail(MapValidationError(validation));

        var gte = input.Filter?.CreatedAtGte;
        var lte = input.Filter?.CreatedAtLte;

        return await ResultExtensions.TryAsync(async ct =>
        {
            if (isDomainWide)
            {
                var union = await GetJobsAcrossDomainAsync(input.Domain, gte, lte, ct);
                return new MonitorPagedResponse<MonitorJobItem>
                {
                    Items = union.Select(MapItem).ToList()
                };
            }

            var skip = (input.Page - 1) * input.PageSize;
            var rows = await jobRepository.GetActiveByFlowPagedAsync(
                input.Workflow!, gte, lte, skip, input.PageSize, ct);

            var hasNext = rows.Count > input.PageSize;
            var items = rows.Take(input.PageSize).Select(MapItem).ToList();

            return new MonitorPagedResponse<MonitorJobItem>
            {
                Pagination = new MonitorPaginationInfo
                {
                    Page     = input.Page,
                    PageSize = input.PageSize,
                    HasNext  = hasNext
                },
                Items = items
            };
        }, cancellationToken);
    }

    private static MonitorJobItem MapItem(InstanceJob j) => new()
    {
        JobId      = j.JobId,
        Name       = j.JobName,
        InstanceId = j.InstanceId,
        Flow       = j.FlowName,
        Domain     = j.Domain,
        IsActive   = j.IsActive,
        CreatedAt  = j.CreatedAt,
        ModifiedAt = j.ModifiedAt
    };

    private static Error MapValidationError(JobsFilterValidation validation) => validation switch
    {
        JobsFilterValidation.CreatedAtRequired => Error.Validation(
            "jobs.createdAtRequired",
            "A bounded createdAt range (both createdAt[gte] and createdAt[lte]) is required for domain-wide job queries."),
        JobsFilterValidation.CreatedAtRange => Error.Validation(
            "jobs.createdAtRange",
            "createdAt requires both bounds together and createdAt[gte] must not be greater than createdAt[lte]."),
        _ => throw new InvalidOperationException($"Unhandled {nameof(JobsFilterValidation)}: {validation}")
    };

    private async Task<List<InstanceJob>> GetJobsAcrossDomainAsync(
        string domain, DateTime? createdAtGte, DateTime? createdAtLte, CancellationToken ct)
    {
        var workflowKeys = await GetWorkflowKeysForDomainAsync(domain, ct);
        if (workflowKeys.Count == 0)
            return [];

        var perSchema = await Task.WhenAll(
            workflowKeys.Select(key => GetJobsInIsolatedSchemaAsync(key, createdAtGte, createdAtLte, ct)));
        return [.. perSchema.SelectMany(j => j)];
    }

    private async Task<List<InstanceJob>> GetJobsInIsolatedSchemaAsync(
        string schemaKey, DateTime? createdAtGte, DateTime? createdAtLte, CancellationToken ct)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var repo          = scope.ServiceProvider.GetRequiredService<IInstanceJobRepository>();

        using (currentSchema.Use(schemaKey))
            return await repo.GetActiveByFlowAsync(schemaKey, createdAtGte, createdAtLte, ct);
    }

    private async Task<IReadOnlyList<string>> GetWorkflowKeysForDomainAsync(string domain, CancellationToken ct)
    {
        // ICacheSet exposes only per-key lookups (no domain-wide enumeration), so workflow keys
        // for the domain are sourced from the runtime backend (DB) in an isolated scope.
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();
        var fromDb = (await runtimeService.GetAsync<BBT.Workflow.Definitions.Workflow>(ct)).ToList();
        return fromDb
            .Where(w => w is not null
                        && !string.IsNullOrWhiteSpace(w.Key)
                        && string.Equals(w.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
