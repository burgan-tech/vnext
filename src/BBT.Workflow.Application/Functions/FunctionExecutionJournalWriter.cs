using System.Threading.Channels;
using BBT.Aether.Guids;
using BBT.Aether.Uow;
using BBT.Workflow.Logging;
using BBT.Workflow.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Functions;

/// <summary>
/// Background consumer of the function-execution journal (vnext-client-sdk-core#60). Drains the bounded
/// queue behind <see cref="FunctionExecutionJournal"/> in batches and bulk-inserts them on its own
/// short-lived DI scope, so the durable write happens entirely off the function's request path.
/// </summary>
/// <remarks>
/// <para>
/// Why this shape, not Dapr: the write must not affect function execution time and 100% completeness is
/// explicitly not required. Using the Dapr scheduler for per-invocation journaling was a deliberate
/// non-choice — it keeps high-volume telemetry off the scheduler. An in-process bounded channel + a
/// single batching consumer gives a non-blocking producer, one SaveChanges per batch, and graceful
/// shedding when overloaded — with no external dependency in the write path.
/// </para>
/// <para>
/// Best-effort throughout: a persistence failure is logged and swallowed so the loop keeps running, and
/// on shutdown the queue is completed and whatever remains is fully drained. Registered by the
/// Orchestration host — the only host that executes domain functions. Batch size is read from
/// <see cref="FunctionExecutionJournalOptions"/> each drain cycle via <c>IOptionsMonitor</c>, so it is
/// tunable at runtime; queue capacity is fixed at channel creation (see the options remarks).
/// </para>
/// </remarks>
public sealed class FunctionExecutionJournalWriter(
    FunctionExecutionJournal journal,
    IServiceScopeFactory scopeFactory,
    IGuidGenerator guidGenerator,
    IOptionsMonitor<FunctionExecutionJournalOptions> options,
    ILogger<FunctionExecutionJournalWriter> logger) : BackgroundService
{
    private long _lastReportedDropped;

    /// <summary>Current batch cap, re-read from config each drain cycle; clamped to at least 1.</summary>
    private int CurrentBatchSize => Math.Max(1, options.CurrentValue.BatchSize);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = journal.Reader;
        var batch = new List<FunctionExecutionRecord>();

        try
        {
            // WaitToReadAsync completes false once the queue is Completed (shutdown), or throws on cancel.
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                batch.Clear();
                DrainInto(reader, batch, CurrentBatchSize);   // re-read the cap each cycle (live-tunable)
                if (batch.Count > 0)
                {
                    // CancellationToken.None (not stoppingToken): a batch already pulled off the queue is
                    // no longer in the channel, so cancelling its write mid-shutdown would LOSE it (and log
                    // a spurious failure). An orderly shutdown lets the in-flight write complete; the host's
                    // own shutdown timeout is the only bound.
                    await FlushAsync(batch, CancellationToken.None);
                }

                ReportDrops();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping — fall through to the final drain below.
        }

        // Final drain: StopAsync completed the queue (or the token cancelled). A cancelled token makes
        // WaitToReadAsync throw even while items remain, so THIS loop — not the main loop — is what
        // actually guarantees the drain. Flush EVERYTHING left, in batch-sized chunks, so an orderly
        // shutdown does not silently lose already-enqueued rows. Finite: the queue is Completed by
        // StopAsync before this runs, so no new items can arrive and the backlog is bounded by capacity.
        while (true)
        {
            batch.Clear();
            DrainInto(reader, batch, CurrentBatchSize);
            if (batch.Count == 0)
            {
                break;
            }

            await FlushAsync(batch, CancellationToken.None);
        }

        ReportDrops();
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting new records and let ExecuteAsync drain the remainder before it returns.
        journal.Complete();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>Pulls up to <paramref name="batchSize"/> records already sitting in the queue, without waiting.</summary>
    private static void DrainInto(
        ChannelReader<FunctionExecutionRecord> reader, List<FunctionExecutionRecord> batch, int batchSize)
    {
        while (batch.Count < batchSize && reader.TryRead(out var record))
        {
            batch.Add(record);
        }
    }

    /// <summary>
    /// Persists one batch on a fresh DI scope inside its own (RequiresNew, non-transactional) unit of
    /// work. Any failure is logged and swallowed — a telemetry write must never crash the writer loop
    /// nor affect the functions it records.
    /// </summary>
    private async Task FlushAsync(IReadOnlyList<FunctionExecutionRecord> batch, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var repository = scope.ServiceProvider.GetRequiredService<IFunctionExecutionRepository>();

            var executions = new List<FunctionExecution>(batch.Count);
            foreach (var record in batch)
            {
                executions.Add(ToEntity(record));
            }

            // RequiresNew so this stands entirely apart from any ambient scope; non-transactional because
            // best-effort journaling needs no cross-statement atomicity. The UoW is also what lets the
            // repository resolve its DbContext (Aether binds the context lifetime to an active UoW).
            await using var uow = uowManager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = false
            });
            await repository.InsertBatchAsync(executions, cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.FunctionExecutionJournalBatchWriteFailed(ex, batch.Count);
        }
    }

    /// <summary>Maps a captured record to the journal entity, minting its id from the Aether generator.</summary>
    private FunctionExecution ToEntity(FunctionExecutionRecord record) =>
        FunctionExecution.Record(
            guidGenerator.Create(),
            record.Domain,
            record.FunctionKey,
            record.FunctionVersion,
            record.Scope,
            record.Workflow,
            record.InstanceId,
            record.InvokedAt,
            record.DurationMs,
            record.Succeeded,
            record.StatusCode,
            record.ErrorCode,
            record.FromCache,
            record.InvokedBy,
            record.InvokedByBehalfOf,
            record.TraceId);

    /// <summary>
    /// Reports newly-dropped records off the hot path (the producer only increments a counter). Logged as
    /// a running total so a full queue under load is visible without per-record logging.
    /// </summary>
    private void ReportDrops()
    {
        var dropped = journal.DroppedCount;
        if (dropped > _lastReportedDropped)
        {
            logger.FunctionExecutionJournalRecordsDropped(dropped - _lastReportedDropped, dropped);
            _lastReportedDropped = dropped;
        }
    }
}
