using System.Data;
using System.Text;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Metrics;

/// <summary>
/// EF Core implementation of <see cref="IFunctionExecutionRepository"/> over
/// <see cref="MetricsDbContext"/> (fixed <c>sys_metrics</c> schema).
/// </summary>
/// <remarks>
/// The context is registered with schema switching, so every EF operation is pinned to
/// <see cref="FunctionExecution.SchemaName"/> for the call: a write happens inside a function's
/// <c>sys_functions</c> scope, and without the pin the command would target that ambient schema
/// instead. The raw summary query names the schema literally and needs no pin.
/// </remarks>
public class EfCoreFunctionExecutionRepository(
    IAetherDbContextProvider<MetricsDbContext> dbContext,
    IServiceProvider serviceProvider,
    ICurrentSchema currentSchema)
    : EfCoreRepository<MetricsDbContext, FunctionExecution, Guid>(dbContext, serviceProvider),
        IFunctionExecutionRepository
{
    /// <inheritdoc />
    public async Task InsertBatchAsync(
        IReadOnlyCollection<FunctionExecution> executions,
        CancellationToken cancellationToken = default)
    {
        if (executions.Count == 0)
        {
            return;
        }

        using (currentSchema.Change(FunctionExecution.SchemaName))
        {
            // One AddRange + one SaveChanges for the whole batch — the throughput point of the async
            // journal. Runs inside the (RequiresNew, non-transactional) unit of work the background
            // writer opens, which is also what lets GetDbContextAsync resolve the context (Aether binds
            // the DbContext lifetime to an active UoW). The schema pin binds that context to sys_metrics,
            // regardless of any ambient schema.
            var context = await GetDbContextAsync();
            var dbSet = await GetDbSetAsync();
            await dbSet.AddRangeAsync(executions, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<FunctionExecutionQueryResult> QueryAsync(
        FunctionExecutionQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 20 : query.PageSize;

        using (currentSchema.Change(FunctionExecution.SchemaName))
        {
            var dbSet = await GetDbSetAsync();
            var filtered = ApplyFilter(dbSet.AsNoTracking(), query);

            // Fetch one row beyond the page to decide HasNext without a second COUNT round-trip.
            var rows = await filtered
                .OrderByDescending(e => e.InvokedAt)
                .ThenByDescending(e => e.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize + 1)
                .ToListAsync(cancellationToken);

            var hasNext = rows.Count > pageSize;
            if (hasNext)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            return new FunctionExecutionQueryResult(rows, hasNext);
        }
    }

    /// <inheritdoc />
    public async Task<FunctionExecutionSummary> SummarizeAsync(
        FunctionExecutionQuery query,
        CancellationToken cancellationToken = default)
    {
        // percentile_cont has no LINQ translation, so the aggregate is one raw statement over the same
        // filter as QueryAsync. Count / p50 / p95 / failure-rate in a single round-trip. Pin the schema
        // like the other reads: resolving the context requires a current schema to be set, and the read
        // endpoint runs with none.
        using var schemaScope = currentSchema.Change(FunctionExecution.SchemaName);

        var where = new StringBuilder("\"FunctionKey\" = @key");
        var context = await GetDbContextAsync();
        var connection = context.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        AddParameter(command, "key", query.FunctionKey);

        if (query.Workflow is { Length: > 0 })
        {
            where.Append(" AND \"Workflow\" = @wf");
            AddParameter(command, "wf", query.Workflow);
        }
        if (query.From is { } from)
        {
            where.Append(" AND \"InvokedAt\" >= @from");
            AddParameter(command, "from", from);
        }
        if (query.To is { } to)
        {
            where.Append(" AND \"InvokedAt\" <= @to");
            AddParameter(command, "to", to);
        }
        if (query.Succeeded is { } ok)
        {
            where.Append(" AND \"Succeeded\" = @ok");
            AddParameter(command, "ok", ok);
        }

        command.CommandText = $"""
            SELECT count(*)::bigint AS c,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY "DurationMs") AS p50,
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY "DurationMs") AS p95,
                   (count(*) FILTER (WHERE NOT "Succeeded"))::double precision / NULLIF(count(*), 0) AS fr
            FROM sys_metrics."FunctionExecutions"
            WHERE {where}
            """;

        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var count = reader.GetInt64(0);
                double? p50 = reader.IsDBNull(1) ? null : reader.GetDouble(1);
                double? p95 = reader.IsDBNull(2) ? null : reader.GetDouble(2);
                var failureRate = reader.IsDBNull(3) ? 0d : reader.GetDouble(3);
                return new FunctionExecutionSummary(count, p50, p95, failureRate);
            }

            return new FunctionExecutionSummary(0, null, null, 0);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static IQueryable<FunctionExecution> ApplyFilter(
        IQueryable<FunctionExecution> source, FunctionExecutionQuery query)
    {
        source = source.Where(e => e.FunctionKey == query.FunctionKey);
        if (query.Workflow is { Length: > 0 })
        {
            source = source.Where(e => e.Workflow == query.Workflow);
        }
        if (query.From is { } from)
        {
            source = source.Where(e => e.InvokedAt >= from);
        }
        if (query.To is { } to)
        {
            source = source.Where(e => e.InvokedAt <= to);
        }
        if (query.Succeeded is { } ok)
        {
            source = source.Where(e => e.Succeeded == ok);
        }

        return source;
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
