using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitor.Common.DTOs;
using BBT.Workflow.Monitor.Instances.DTOs;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>
/// Cross-schema orchestrator for the domain-wide faulted-instances query. Enumerates the domain's
/// workflow schemas, fans out (bounded concurrency) to the existing single-schema instance query
/// inside isolated DI scopes, and unions the results. Strictly read-only.
/// </summary>
public sealed class MonitorDomainFaultService(
    IServiceProvider serviceProvider,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<MonitorDomainFaultService> logger)
    : ApplicationService(serviceProvider), IMonitorDomainFaultService
{
    private const int MaxConcurrentSchemas = 8;
    private const int SchemaPageSize = 100;
    private const int MaxItemsPerSchema = 1000;
    private const string CreatedAtDescSort = """{"field":"createdAt","direction":"desc"}""";

    /// <inheritdoc />
    public async Task<Result<MonitorPagedResponse<MonitorInstanceResponse>>> GetDomainFaultedInstancesAsync(
        MonitorGetDomainFaultedInput input,
        CancellationToken cancellationToken = default)
    {
        var (validation, effectiveFilter) = FaultedFilterValidator.BuildEffectiveFilter(input.Filter);
        if (validation != FaultedFilterValidation.Valid || effectiveFilter is null)
            return Result<MonitorPagedResponse<MonitorInstanceResponse>>.Fail(MapValidationError(validation));

        var workflowKeys = await GetWorkflowKeysForDomainAsync(input.Domain, cancellationToken);
        if (workflowKeys.Count == 0)
            return Result<MonitorPagedResponse<MonitorInstanceResponse>>.Ok(
                new MonitorPagedResponse<MonitorInstanceResponse> { Items = [] });

        var gate = new SemaphoreSlim(MaxConcurrentSchemas);
        try
        {
            var perSchema = await Task.WhenAll(workflowKeys.Select(async key =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await FetchFaultedInSchemaAsync(key, input.Domain, effectiveFilter, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }));

            foreach (var schemaResult in perSchema)
                if (!schemaResult.IsSuccess)
                    return Result<MonitorPagedResponse<MonitorInstanceResponse>>.Fail(schemaResult.Error);

            // Per-schema results arrive pre-sorted; re-sort globally to merge across schemas.
            var union = perSchema
                .SelectMany(r => r.Value ?? [])
                .OrderByDescending(i => i.Metadata?.CreatedAt ?? DateTime.MinValue)
                .ToList();

            return Result<MonitorPagedResponse<MonitorInstanceResponse>>.Ok(
                new MonitorPagedResponse<MonitorInstanceResponse> { Items = union });
        }
        finally
        {
            gate.Dispose();
        }
    }

    private static Error MapValidationError(FaultedFilterValidation validation) => validation switch
    {
        FaultedFilterValidation.FilterRequired => Error.Validation(
            "filter.required", "A filter with a bounded createdAt range is required."),
        FaultedFilterValidation.FilterInvalid => Error.Validation(
            "filter.invalid", "The filter is not valid GraphQL JSON."),
        FaultedFilterValidation.CreatedAtRangeRequired => Error.Validation(
            "filter.createdAtRequired", "A bounded createdAt range (both a lower and an upper bound) is required."),
        FaultedFilterValidation.StatusNotAllowed => Error.Validation(
            "filter.statusNotAllowed", "The status field is managed by this endpoint and must not be supplied."),
        _ => Error.Validation("filter.invalid", "The filter is not valid.")
    };

    private async Task<IReadOnlyList<string>> GetWorkflowKeysForDomainAsync(string domain, CancellationToken ct)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();
        var fromDb = (await runtimeService.GetAsync<Definitions.Workflow>(ct)).ToList();
        return fromDb
            .Where(w => w is not null
                        && !string.IsNullOrWhiteSpace(w.Key)
                        && string.Equals(w.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Select(w => w!.Key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Result<List<MonitorInstanceResponse>>> FetchFaultedInSchemaAsync(
        string workflowKey, string domain, string effectiveFilter, CancellationToken ct)
    {
        var collected = new List<MonitorInstanceResponse>();
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IMonitorInstanceQueryService>();

        using (currentSchema.Change(workflowKey))
        {
            var page = 1;
            while (true)
            {
                var listInput = new MonitorGetInstancesInput
                {
                    Domain = domain,
                    Workflow = workflowKey,
                    Filter = effectiveFilter,
                    Sort = CreatedAtDescSort,
                    Page = page,
                    PageSize = SchemaPageSize
                };

                var result = await queryService.GetInstancesAsync(listInput, ct);
                if (!result.IsSuccess)
                    return Result<List<MonitorInstanceResponse>>.Fail(result.Error);

                var pageResponse = result.Value;
                if (pageResponse is null)
                    break;

                foreach (var item in pageResponse.Items)
                    // GroupBy is never set for this query, so every item is a MonitorInstanceResponse.
                    if (item is MonitorInstanceResponse instance)
                        collected.Add(instance);

                if (collected.Count >= MaxItemsPerSchema)
                {
                    if (collected.Count > MaxItemsPerSchema)
                        collected.RemoveRange(MaxItemsPerSchema, collected.Count - MaxItemsPerSchema);

                    logger.LogWarning(
                        "Domain-wide faulted scan hit the per-schema cap of {Cap} for workflow {Workflow}; results truncated for this schema.",
                        MaxItemsPerSchema, workflowKey);
                    break;
                }

                if (pageResponse.Pagination?.HasNext != true)
                    break;

                page++;
            }
        }

        return Result<List<MonitorInstanceResponse>>.Ok(collected);
    }
}
