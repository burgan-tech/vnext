using BBT.Aether.BackgroundJob;
using BBT.Aether.BackgroundJob.Processing;
using BBT.Aether.Clock;
using BBT.Aether.Events;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Data;

/// <summary>
/// Runs Aether's outbox processor once per database under multi-domain hosting with dedicated
/// databases: each database's <c>sys_queues</c> holds the outbox rows its domains wrote in the same
/// transaction as their data.
/// </summary>
/// <remarks>
/// A decorator, not a new poller: Aether's <c>OutboxBackgroundService</c> keeps its adaptive polling and
/// calls this once per cycle. With one database (no <c>ConnectionStrings:Domains</c>) it is a plain
/// pass-through.
/// </remarks>
public sealed class DomainFanOutOutboxProcessor(IOutboxProcessor inner, IDomainDatabaseResolver resolver)
    : IOutboxProcessor
{
    /// <inheritdoc />
    public Task<int> RunAsync(CancellationToken cancellationToken = default) =>
        DomainDatabaseFanOut.RunAsync(resolver, ct => inner.RunAsync(ct), cancellationToken);
}

/// <summary>
/// Arms background jobs once per database under multi-domain hosting with dedicated databases (each
/// database's <c>sys_queues.BackgroundJobs</c>). With one database it behaves exactly like the base class.
/// </summary>
public sealed class DomainFanOutBackgroundJobArmingProcessor(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    BackgroundJobOptions options,
    ILogger<BackgroundJobArmingProcessor> logger,
    IDomainDatabaseResolver resolver)
    : BackgroundJobArmingProcessor(scopeFactory, clock, options, logger)
{
    /// <inheritdoc />
    public override Task RunAsync(CancellationToken ct = default) =>
        DomainDatabaseFanOut.RunAsync(resolver, async token =>
        {
            await base.RunAsync(token);
            return 0;
        }, ct);
}

/// <summary>Runs per-database work once for each distinct database, under one of its domains' scope.</summary>
public static class DomainDatabaseFanOut
{
    /// <summary>
    /// Runs <paramref name="run"/> once per <see cref="IDomainDatabaseResolver.DatabaseRepresentatives"/>
    /// entry and sums the results; a single database runs it once with no scope, as before.
    /// </summary>
    public static async Task<int> RunAsync(
        IDomainDatabaseResolver resolver,
        Func<CancellationToken, Task<int>> run,
        CancellationToken cancellationToken)
    {
        var databases = resolver.DatabaseRepresentatives;
        if (databases.Count <= 1)
        {
            return await run(cancellationToken);
        }

        // A failing database must not starve the others: every database runs each cycle, and the first
        // failure is rethrown afterwards so the caller's error handling and backoff still see it.
        var total = 0;
        Exception? firstFailure = null;
        foreach (var domain in databases)
        {
            try
            {
                using (DomainScope.Begin(domain))
                {
                    total += await run(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }

        return total;
    }
}
