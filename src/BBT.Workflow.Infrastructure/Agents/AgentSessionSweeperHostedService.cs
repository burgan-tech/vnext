using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Instances.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Agents;

/// <summary>
/// Deletes expired agent sessions (issue #1105) every <c>AiAgent:SessionSweepIntervalMinutes</c>: finds the
/// schemas that have an <c>AgentSessions</c> table, then deletes in batches of 1000 rows per statement so
/// no long lock is held. Disabled when the interval is 0.
/// </summary>
public sealed class AgentSessionSweeperHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AiAgentOptions> options) : BackgroundService
{
    private const int BatchSize = 1000;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.SessionSweepIntervalMinutes;
        if (minutes <= 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAllAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) // skipcq: CS-R1008 — a failed sweep waits for the next tick
            {
                // A failed sweep is retried on the next tick; expired rows are also ignored on read.
            }
        }
    }

    private async Task SweepAllAsync(CancellationToken cancellationToken)
    {
        foreach (var schema in await ListSchemasAsync(cancellationToken))
        {
            // A full batch means more expired rows may be left in this schema.
            var deleted = BatchSize;
            while (deleted == BatchSize && !cancellationToken.IsCancellationRequested)
                deleted = await SweepBatchAsync(schema, cancellationToken);
        }
    }

    /// <summary>Schemas that hold an <c>AgentSessions</c> table.</summary>
    private async Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = await scope.ServiceProvider.GetRequiredService<IAetherDbContextProvider<WorkflowDbContext>>()
            .GetDbContextAsync(cancellationToken);
        return await context.Database
            .SqlQueryRaw<string>("SELECT table_schema AS \"Value\" FROM information_schema.tables WHERE table_name = 'AgentSessions'")
            .ToListAsync(cancellationToken);
    }

    /// <summary>Deletes one batch of expired rows in a schema; returns how many it deleted.</summary>
    private async Task<int> SweepBatchAsync(string schema, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<ICurrentSchema>().Change(schema); // skipcq: CS-W1100 — the schema scope ends on dispose
        return await scope.ServiceProvider.GetRequiredService<IAgentSessionStore>().SweepExpiredAsync(BatchSize, cancellationToken);
    }
}
