using System;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the production regression behind <c>Npgsql 23505: duplicate key value violates unique
/// constraint "UX_InstanceTasks_ExecutionKey"</c> (live trace <c>0a3674d7fd8f1e5bd324b43f0d4dbee8</c>,
/// transition <c>document-ready-update</c>). The transition's <c>onExecute</c> list carried the same
/// task key (<c>script-task</c>) twice. Both occurrences shared the same freshly created transition
/// record, so both computed the identical <see cref="InstanceTask.ExecutionKey"/> — the old hash was
/// <c>SHA256(transitionId:taskId)</c>, which identifies the (transition, task) PAIR rather than one
/// OCCURRENCE. The first insert succeeded and completed; the second died at its <c>Db.INSERT</c>
/// before it could even prepare its input, faulting the instance and cascading the fault to the
/// parent flow. The fix folds <see cref="TaskTrigger"/> and the task's <c>order</c> into the hash so
/// the key identifies one occurrence — order alone is insufficient because the same task key can
/// also repeat across different hooks (onExecute / onEntry / onExit) of the same transition.
/// </summary>
public sealed class InstanceTaskExecutionKeyTests
{
    /// <summary>
    /// This is the production bug, reproduced directly: two occurrences of the SAME task key in the
    /// SAME transition, under the SAME trigger, distinguished only by their list position (Order).
    /// Before the fix these collide (same key), which is exactly what causes the duplicate-key
    /// INSERT to fault the second occurrence in production.
    /// </summary>
    [Fact]
    public void CreateExecutionKey_SameTaskSameTriggerDifferentOrder_ProducesDifferentKeys()
    {
        var transitionId = Guid.NewGuid();
        const string taskId = "script-task";

        var firstOccurrence = InstanceTask.CreateExecutionKey(transitionId, taskId, TaskTrigger.OnExecute, 0);
        var secondOccurrence = InstanceTask.CreateExecutionKey(transitionId, taskId, TaskTrigger.OnExecute, 1);

        firstOccurrence.ShouldNotBe(secondOccurrence);
    }

    /// <summary>
    /// A separately recorded trap in this codebase: the same task key can appear under different
    /// hooks of the same transition (e.g. bound as both an OnExecute and an OnEntry task) at the
    /// same list position. Order alone would not disambiguate this case, so trigger must be folded
    /// in too.
    /// </summary>
    [Fact]
    public void CreateExecutionKey_SameTaskSameOrderDifferentTrigger_ProducesDifferentKeys()
    {
        var transitionId = Guid.NewGuid();
        const string taskId = "shared-task";

        var onExecuteKey = InstanceTask.CreateExecutionKey(transitionId, taskId, TaskTrigger.OnExecute, 0);
        var onEntryKey = InstanceTask.CreateExecutionKey(transitionId, taskId, TaskTrigger.OnEntry, 0);

        onExecuteKey.ShouldNotBe(onEntryKey);
    }

    /// <summary>
    /// Genuine retries must still be idempotent: an identical (transitionId, taskId, taskTrigger,
    /// order) tuple — the same occurrence, re-attempted — must produce the SAME key. This is what
    /// lets <c>FindByTransitionAndTaskAsync</c>'s idempotency probe find and reuse the previous
    /// attempt's journal row instead of inserting a duplicate.
    /// </summary>
    [Fact]
    public void CreateExecutionKey_IdenticalOccurrence_ProducesSameKey()
    {
        var transitionId = Guid.NewGuid();
        const string taskId = "script-task";

        var first = InstanceTask.CreateExecutionKey(transitionId, taskId, TaskTrigger.OnExecute, 3);
        var second = InstanceTask.CreateExecutionKey(transitionId, taskId, TaskTrigger.OnExecute, 3);

        first.ShouldBe(second);
    }

    /// <summary>
    /// End-to-end sanity check via the constructor (not just the static hash helper): building two
    /// <see cref="InstanceTask"/> instances the way <c>TaskExecutionEngine</c> does for two entries of
    /// the same task key at different orders must not collide.
    /// </summary>
    [Fact]
    public void Constructor_TwoOccurrencesOfSameTaskKeyInSameTransition_GetDistinctExecutionKeys()
    {
        var transitionId = Guid.NewGuid();
        const string taskId = "script-task";

        var first = new InstanceTask(Guid.NewGuid(), transitionId, taskId, TaskTrigger.OnExecute, 0);
        var second = new InstanceTask(Guid.NewGuid(), transitionId, taskId, TaskTrigger.OnExecute, 1);

        first.ExecutionKey.ShouldNotBeNull();
        second.ExecutionKey.ShouldNotBeNull();
        first.ExecutionKey.ShouldNotBe(second.ExecutionKey);
    }
}
