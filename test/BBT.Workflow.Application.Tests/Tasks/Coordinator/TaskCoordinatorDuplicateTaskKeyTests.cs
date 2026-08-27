using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Pins the second, still-open half of the same-key journal regression fixed for DIFFERENT orders
/// by <c>InstanceTask.ExecutionKey</c> (see <c>InstanceTaskExecutionKeyTests</c>). A hook may
/// legitimately list the same task key twice at the SAME order — e.g.
/// <c>[{"key":"script-task","order":0}, {"key":"script-task","order":0}, {"key":"http-task","order":0},
/// {"key":"remote-task","order":1}]</c>. Before this fix, <c>TaskCoordinator.ExecuteWithDetailsAsync</c>
/// grouped both "script-task" entries into the SAME order-0 group and ran them through
/// <c>ExecuteTaskGroupInParallelAsync</c> (<c>Task.WhenAll</c>) with ONE SHARED
/// <c>TaskEngineExecutionOptions</c> instance carrying no <c>JournalTaskKey</c> override — so both
/// occurrences computed the IDENTICAL <c>options.JournalTaskKey ?? task.Key</c> ("script-task") and
/// therefore the identical <c>InstanceTask.ExecutionKey</c> (same transitionId/taskId/trigger/order),
/// racing on the INSERT and faulting the second occurrence on
/// <c>UX_InstanceTasks_ExecutionKey</c> (23505). This is a RACE, not a sequence bug: it was broken
/// with the idempotency probe on or off, since probe-then-insert is not atomic, and predates the
/// recent probe-skipping perf change. The fix reuses the FanOut journal-key-suffix mechanism
/// (<c>TaskEngineExecutionOptions.JournalTaskKey</c>): a repeated key gets a distinct
/// "{key}#{position}" suffix per occurrence, an already-unique key in the group keeps its bare key,
/// and a group in a different order is entirely unaffected.
/// </summary>
public sealed class TaskCoordinatorDuplicateTaskKeyTests
{
    /// <summary>
    /// The user-reported fixture, verbatim: two "script-task" entries at order 0 (the bug), one
    /// unique "http-task" sharing that same order-0 group, and one "remote-task" in its own
    /// order-1 group. Before the fix, the two "script-task" calls receive the exact SAME
    /// <see cref="TaskEngineExecutionOptions"/> instance (both <c>JournalTaskKey == null</c>), which
    /// is precisely what collapses their <c>ExecutionKey</c>s onto one another in production.
    /// </summary>
    [Fact]
    public async Task ExecuteWithDetailsAsync_DuplicateTaskKeySameOrder_GivesEachOccurrenceADistinctJournalKey()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var scriptTaskFirst = WorkflowTaskFactory.CreateHttpTask("script-task");
        var scriptTaskSecond = WorkflowTaskFactory.CreateHttpTask("script-task");
        var httpTask = WorkflowTaskFactory.CreateHttpTask("http-task");
        var remoteTask = WorkflowTaskFactory.CreateHttpTask("remote-task");

        var scriptFirstDef = OnExecuteTask.Create(0, scriptTaskFirst, ScriptCode.FromNative(string.Empty));
        var scriptSecondDef = OnExecuteTask.Create(0, scriptTaskSecond, ScriptCode.FromNative(string.Empty));
        var httpDef = OnExecuteTask.Create(0, httpTask, ScriptCode.FromNative(string.Empty));
        var remoteDef = OnExecuteTask.Create(1, remoteTask, ScriptCode.FromNative(string.Empty));

        var definitions = new[] { scriptFirstDef, scriptSecondDef, httpDef, remoteDef };

        var observedOptions = new ConcurrentDictionary<OnExecuteTask, TaskEngineExecutionOptions>();
        StubEngine(engine, observedOptions);

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);
        var context = CreateContext();

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions,
            Guid.NewGuid(),
            TaskTrigger.OnExecute,
            TaskExecutionOrigin.Flow,
            context);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        observedOptions.Count.ShouldBe(4);

        // The bug: before the fix these two are the SAME options instance (both JournalTaskKey ==
        // null), so their downstream ExecutionKey collides. After the fix each occurrence gets a
        // distinct, position-based suffix — assigned from the definition's list position, not from
        // which of the two happens to finish first in the parallel race.
        observedOptions[scriptFirstDef].JournalTaskKey.ShouldBe("script-task#0");
        observedOptions[scriptSecondDef].JournalTaskKey.ShouldBe("script-task#1");
        observedOptions[scriptFirstDef].JournalTaskKey.ShouldNotBe(observedOptions[scriptSecondDef].JournalTaskKey);

        // http-task is unique within its order-0 group despite sharing the group with the
        // duplicated key — it must keep its bare key, no suffix churn for the common case.
        observedOptions[httpDef].JournalTaskKey.ShouldBeNull();

        // remote-task lives in its own order-1 group (the sequential/single-task path) and must be
        // completely unaffected by the duplicate in order-0.
        observedOptions[remoteDef].JournalTaskKey.ShouldBeNull();
    }

    /// <summary>
    /// A hook with no repeated key must not pay any journal-key churn: every call receives the
    /// exact same shared preset instance the coordinator already builds once per call
    /// (<c>TaskEngineExecutionOptions.Default</c>/<c>FreshTransitionRecord</c>), by reference.
    /// </summary>
    [Fact]
    public async Task ExecuteWithDetailsAsync_NoDuplicateTaskKeys_ReusesSharedOptionsInstance()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var firstTask = WorkflowTaskFactory.CreateHttpTask("first");
        var secondTask = WorkflowTaskFactory.CreateHttpTask("second");
        var definitions = new[]
        {
            OnExecuteTask.Create(0, firstTask, ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(0, secondTask, ScriptCode.FromNative(string.Empty))
        };

        var observedOptions = new ConcurrentBag<TaskEngineExecutionOptions>();
        engine.ExecuteAsync(
                Arg.Any<OnExecuteTask>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<TaskEngineExecutionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var definition = call.Arg<OnExecuteTask>();
                observedOptions.Add(call.Arg<TaskEngineExecutionOptions>());
                return Task.FromResult(Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
                    [TaskExecutionSummary.Success(definition.Task.Key, "Http")])));
            });

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);
        var context = CreateContext();

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, context);

        result.IsSuccess.ShouldBeTrue();
        observedOptions.Count.ShouldBe(2);
        var distinctInstances = observedOptions.Select(o => (object)o).Distinct().Count();
        distinctInstances.ShouldBe(1, "unique keys must not churn a new options instance");
        observedOptions.ShouldAllBe(o => o.JournalTaskKey == null);
    }

    /// <summary>
    /// Same-key/same-order is a legitimate (now-working) shape but still almost certainly an
    /// authoring mistake, so it must be surfaced via <c>WorkflowLogs.DuplicateTaskKeyAtSameOrder</c>
    /// (Warning, EventId 10155) naming the transition, hook, task key and order.
    /// </summary>
    [Fact]
    public async Task ExecuteWithDetailsAsync_DuplicateTaskKeySameOrder_LogsWarningWithTransitionHookKeyAndOrder()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var scriptTaskFirst = WorkflowTaskFactory.CreateHttpTask("script-task");
        var scriptTaskSecond = WorkflowTaskFactory.CreateHttpTask("script-task");
        var definitions = new[]
        {
            OnExecuteTask.Create(0, scriptTaskFirst, ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(0, scriptTaskSecond, ScriptCode.FromNative(string.Empty))
        };

        var observedOptions = new ConcurrentDictionary<OnExecuteTask, TaskEngineExecutionOptions>();
        StubEngine(engine, observedOptions);

        // IsEnabled must be stubbed true: every WorkflowLogs extension is source-generated with an
        // IsEnabled guard, and a substitute's default bool is FALSE — leaving it would make the
        // logger silently swallow the call regardless of what LogDuplicateTaskKeysIfAny does.
        var logger = Substitute.For<ILogger<TaskCoordinator>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services, logger);
        var context = CreateContext(transitionKey: "document-ready-update");

        await coordinator.ExecuteWithDetailsAsync(
            definitions, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, context);

        var fields = LoggedFields(logger, 10155);
        fields["TransitionKey"].ShouldBe("document-ready-update");
        fields["Hook"].ShouldBe(TaskTrigger.OnExecute.ToString());
        fields["TaskKey"].ShouldBe("script-task");
        fields["OccurrenceCount"].ShouldBe(2);
        fields["Order"].ShouldBe(0);
    }

    /// <summary>
    /// A normal hook — no repeated key at the same order — must not log the warning at all.
    /// </summary>
    [Fact]
    public async Task ExecuteWithDetailsAsync_NoDuplicateTaskKeys_DoesNotLogWarning()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var firstTask = WorkflowTaskFactory.CreateHttpTask("first");
        var secondTask = WorkflowTaskFactory.CreateHttpTask("second");
        var definitions = new[]
        {
            OnExecuteTask.Create(0, firstTask, ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(1, secondTask, ScriptCode.FromNative(string.Empty))
        };

        var observedOptions = new ConcurrentDictionary<OnExecuteTask, TaskEngineExecutionOptions>();
        StubEngine(engine, observedOptions);

        var logger = Substitute.For<ILogger<TaskCoordinator>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services, logger);
        var context = CreateContext();

        await coordinator.ExecuteWithDetailsAsync(
            definitions, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, context);

        logger.ReceivedCalls()
            .Any(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                         && call.GetArguments()[1] is EventId id
                         && id.Id == 10155)
            .ShouldBeFalse();
    }

    private static void StubEngine(
        ITaskExecutionEngine engine,
        ConcurrentDictionary<OnExecuteTask, TaskEngineExecutionOptions> observedOptions)
    {
        engine.ExecuteAsync(
                Arg.Any<OnExecuteTask>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<TaskEngineExecutionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var definition = call.Arg<OnExecuteTask>();
                observedOptions[definition] = call.Arg<TaskEngineExecutionOptions>();
                return Task.FromResult(Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
                    [TaskExecutionSummary.Success(definition.Task.Key, "Http")])));
            });
    }

    /// <summary>
    /// Reads the structured fields of the single logged entry carrying <paramref name="eventId"/>,
    /// the way <c>FanOutTestFixture.LoggedFields</c> does for the executor's own logger — through
    /// the state object every <c>LoggerMessage</c>-generated entry exposes, not the rendered
    /// message, so the assertion pins the VALUE rather than the wording.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> LoggedFields(ILogger logger, int eventId)
    {
        var matches = logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                           && call.GetArguments()[1] is EventId id
                           && id.Id == eventId)
            .ToList();

        matches.Count.ShouldBe(1, $"expected exactly one log entry with EventId {eventId}");

        var state = (IReadOnlyList<KeyValuePair<string, object?>>)matches[0].GetArguments()[2]!;
        return state
            .Where(field => field.Key != "{OriginalFormat}")
            .ToDictionary(field => field.Key, field => field.Value);
    }

    private static TaskCoordinator CreateCoordinator(
        ITaskExecutionEngine engine,
        ServiceProvider services,
        ILogger<TaskCoordinator>? logger = null) => new(
            engine,
            services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IConditionEvaluator>(),
            Substitute.For<ITimerEvaluator>(),
            new ExecutionErrorFactory(new ErrorNormalizer()),
            logger ?? NullLogger<TaskCoordinator>.Instance);

    private static ScriptContext CreateContext(string? transitionKey = null)
    {
        var builder = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>());

        if (transitionKey is not null)
            builder.SetTransition(TransitionFactory.CreateDefault(transitionKey));

        return builder.Build();
    }
}
