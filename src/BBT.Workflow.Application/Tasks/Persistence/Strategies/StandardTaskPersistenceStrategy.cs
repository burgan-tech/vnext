using System.Runtime.CompilerServices;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Persistence;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Tasks.Persistence.Strategies;

/// <summary>
/// Standard implementation of task persistence strategy that handles database persistence
/// for normal workflow task execution (excludes Extension tasks).
/// </summary>
/// <remarks>
/// <para>
/// This strategy is responsible for persisting InstanceTask entities to the database
/// for all TaskTrigger types except Extension. It ensures proper audit trail and
/// workflow execution history is maintained.
/// </para>
/// <para>
/// <b>One write per task.</b> The journal row is written once, at completion, with its final
/// status, business status, request/response and timings — not inserted as "busy" at creation
/// and updated at completion. The hop's unit of work is not transactional, so each of those
/// writes was its own autocommit transaction and its own WAL fsync; at 14 starts/s the task
/// journal alone was 8 of the 48 write commits per start. What is given up is the row's
/// visibility <em>while the task runs</em> (status <c>busy</c> in <c>functions/tasks</c>); what is
/// kept is everything retry relies on: a completed or faulted task always has its row, a task
/// interrupted by a crash has none and is re-executed — exactly what the old "busy" row led to
/// as well (the probe reused the row and ran the task again).
/// </para>
/// </remarks>
public sealed class StandardTaskPersistenceStrategy(
    IInstanceTaskRepository instanceTaskRepository,
    IUnitOfWorkManager unitOfWorkManager,
    IBookkeepingWriter? bookkeepingWriter = null) : ITaskPersistenceStrategy
{
    /// <summary>
    /// Journal entities known to exist in the database — a row the retry probe found and reused.
    /// Completion updates those set-based; everything else is inserted at completion.
    /// </summary>
    private static readonly ConditionalWeakTable<InstanceTask, object> PersistedRows = new();

    /// <summary>
    /// Determines if this strategy should handle the task persistence.
    /// Returns true for all TaskTrigger types except Extension.
    /// </summary>
    /// <param name="origin">The component that initiated task execution.</param>
    /// <returns>True if the task should be persisted to database, false otherwise.</returns>
    public bool CanHandle(TaskExecutionOrigin origin)
    {
        return origin == TaskExecutionOrigin.Flow;
    }

    /// <summary>
    /// Resolves the journal entity a task execution writes to: on a retry, the row a previous
    /// attempt already persisted (found by its <see cref="InstanceTask.ExecutionKey"/>); otherwise
    /// the caller's new entity, which is NOT written here — <see cref="HandleCompletionAsync"/>
    /// inserts it once, with its final state.
    /// </summary>
    /// <param name="instanceTask">The InstanceTask of this execution.</param>
    /// <param name="taskTrigger">
    /// The hook this occurrence runs under. Folded into the idempotency probe's key alongside
    /// <paramref name="order"/> so the probe targets this specific occurrence, not just the
    /// (transition, task) pair — see <see cref="InstanceTask.ExecutionKey"/>.
    /// </param>
    /// <param name="order">The occurrence's execution order within its hook.</param>
    /// <param name="skipLookup">
    /// True when the transition record was freshly inserted by this pipeline run — no journal row
    /// can exist for its id, so the idempotency probe below would be a guaranteed-empty SELECT
    /// per task. Retries pass false and keep the probe, which finds and reuses the previous
    /// attempt's row (including legacy rows without an <c>ExecutionKey</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    public async Task<InstanceTask> HandleCreationAsync(
        InstanceTask instanceTask,
        TaskTrigger taskTrigger,
        int order,
        bool skipLookup = false,
        CancellationToken cancellationToken = default)
    {
        if (skipLookup)
            return instanceTask;

        // No database transaction: task execution for one instance is already serialized by the
        // per-instance Busy/CAS mutex (one owner runs the pipeline at a time — see
        // InstanceBusyManager), so this idempotency read is never racing a concurrent insert the
        // way the accept-time duplicate-job guard is.
        await using var uow = unitOfWorkManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew
        });

        var existing = await instanceTaskRepository.FindByTransitionAndTaskAsync(
            instanceTask.TransitionId,
            instanceTask.TaskId,
            taskTrigger,
            order,
            cancellationToken);
        await uow.CommitAsync(cancellationToken);

        if (existing is null)
            return instanceTask;

        PersistedRows.AddOrUpdate(existing, Persisted);
        return existing;
    }

    /// <summary>
    /// Writes the journal row with its final state: one INSERT for a task executed for the first
    /// time, one set-based UPDATE of the completion columns for a row a retry reused (never an
    /// attach-and-update of the whole row including the jsonb payloads). Runs once per executed
    /// task, so this is the task journal's only hot write.
    /// </summary>
    /// <param name="instanceTask">The InstanceTask to be persisted.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    public async Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
    {
        // No database transaction of its own: a single statement with nothing else to coordinate
        // alongside it. The bookkeeping writer may still wrap it to commit asynchronously.
        await using var uow = unitOfWorkManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew
        });

        var alreadyPersisted = PersistedRows.TryGetValue(instanceTask, out _);
        await RunBookkeepingAsync(async ct =>
        {
            if (alreadyPersisted)
            {
                await instanceTaskRepository.MarkCompletedAsync(instanceTask, ct);
                return;
            }

            await instanceTaskRepository.InsertAsync(instanceTask, true, ct);
            PersistedRows.AddOrUpdate(instanceTask, Persisted);
        }, cancellationToken);

        await uow.CommitAsync(cancellationToken);
    }

    private Task RunBookkeepingAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken) =>
        bookkeepingWriter is null ? write(cancellationToken) : bookkeepingWriter.RunAsync(write, cancellationToken);

    private static readonly object Persisted = new();
}
