using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
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
    public async Task<Result<MonitorActiveJobsResponse>> GetActiveJobsAsync(
        MonitorGetActiveJobsInput input, CancellationToken cancellationToken = default)
    {
        return await ResultExtensions.TryAsync(async ct =>
        {
            var jobs = string.IsNullOrWhiteSpace(input.Workflow)
                ? await GetJobsAcrossDomainAsync(input.Domain, ct)
                : await jobRepository.GetActiveByFlowAsync(input.Workflow, ct);

            return new MonitorActiveJobsResponse
            {
                Jobs = jobs.Select(j => new MonitorJobItem
                {
                    JobId      = j.JobId,
                    Name       = j.JobName,
                    InstanceId = j.InstanceId,
                    Flow       = j.FlowName,
                    Domain     = j.Domain,
                    IsActive   = j.IsActive,
                    CreatedAt  = j.CreatedAt,
                    ModifiedAt = j.ModifiedAt
                }).ToList()
            };
        }, cancellationToken);
    }

    private async Task<List<InstanceJob>> GetJobsAcrossDomainAsync(string domain, CancellationToken ct)
    {
        var workflowKeys = await GetWorkflowKeysForDomainAsync(domain, ct);
        if (workflowKeys.Count == 0)
            return [];

        var perSchema = await Task.WhenAll(workflowKeys.Select(key => GetJobsInIsolatedSchemaAsync(key, ct)));
        return [.. perSchema.SelectMany(j => j)];
    }

    private async Task<List<InstanceJob>> GetJobsInIsolatedSchemaAsync(string schemaKey, CancellationToken ct)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var repo          = scope.ServiceProvider.GetRequiredService<IInstanceJobRepository>();

        using (currentSchema.Use(schemaKey))
            return await repo.GetActiveByFlowAsync(schemaKey, ct);
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
