using BBT.Aether;
using BBT.Aether.Application.Pagination;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Metrics;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Functions;

/// <summary>
/// Reads the function-execution journal for the D metrics endpoints (vnext-client-sdk-core#60):
/// one function's paged run history plus a window summary.
/// </summary>
public interface IFunctionMetricsAppService
{
    /// <summary>Returns one page of a function's executions and the summary over the filtered window.</summary>
    Task<Result<GetFunctionMetricsOutput>> GetFunctionMetricsAsync(
        GetFunctionMetricsInput input,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class FunctionMetricsAppService(
    IRuntimeInfoProvider runtimeInfoProvider,
    IFunctionExecutionRepository repository,
    IUrlTemplateBuilder urlTemplateBuilder,
    IPaginationLinkGenerator paginationLinkGenerator) : IFunctionMetricsAppService
{
    /// <inheritdoc />
    public async Task<Result<GetFunctionMetricsOutput>> GetFunctionMetricsAsync(
        GetFunctionMetricsInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        var page = input.Page < 1 ? 1 : input.Page;
        var pageSize = Math.Clamp(input.PageSize, 1, GetFunctionMetricsInput.MaxPageSize);

        // Normalize the window bounds to UTC before they reach the DB. A `?from=`/`?to=` value bound
        // from the query string without an offset/'Z' arrives as DateTimeKind.Unspecified, and Npgsql
        // rejects an Unspecified DateTime against the timestamptz column (both the LINQ filter and the
        // raw percentile query would throw). Treat a naive value as UTC; convert an explicit local one.
        var query = new FunctionExecutionQuery(
            input.FunctionKey,
            string.IsNullOrEmpty(input.Workflow) ? null : input.Workflow,
            ToUtc(input.From),
            ToUtc(input.To),
            input.Succeeded,
            page,
            pageSize);

        var result = await repository.QueryAsync(query, cancellationToken);
        var summary = await repository.SummarizeAsync(query, cancellationToken);

        var items = result.Items.Select(FunctionExecutionItemDto.FromEntity).ToList();

        // Same paging envelope as the instance queries: a HateoasPagedList carrying HasNext, links
        // generated relative to the metrics route (domain-scoped or flow-scoped).
        var route = string.IsNullOrEmpty(input.Workflow)
            ? urlTemplateBuilder.BuildDomainFunctionUrl(input.Domain, input.FunctionKey) + "/metrics"
            : urlTemplateBuilder.BuildFunctionListUrl(input.Domain, input.Workflow!, input.FunctionKey) + "/metrics";

        var pagedList = new HateoasPagedList<FunctionExecutionItemDto>(items, page, pageSize, result.HasNext);

        return Result<GetFunctionMetricsOutput>.Ok(new GetFunctionMetricsOutput
        {
            Items = items,
            Summary = FunctionMetricsSummaryDto.FromSummary(summary),
            Links = paginationLinkGenerator.Relative().GenerateLinks(pagedList, route)
        });
    }

    private static DateTime? ToUtc(DateTime? value)
    {
        if (value is not { } dateTime)
        {
            return null;
        }

        return dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
    }
}
