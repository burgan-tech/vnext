using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Pins the new per-task options seam on <c>TaskCoordinator.ExecuteWithDetailsAsync</c>: an
/// optional <c>Func&lt;OnExecuteTask, TaskEngineExecutionOptions, TaskEngineExecutionOptions&gt;</c>
/// refiner a caller (the extension path, wired in a later task) can supply to layer a per-task
/// <see cref="TaskEngineExecutionOptions.ResponseVariableKey"/> on top of whatever this call would
/// otherwise resolve — WITHOUT disturbing the existing duplicate-task-key
/// <see cref="TaskEngineExecutionOptions.JournalTaskKey"/> disambiguation
/// (<see cref="TaskCoordinatorDuplicateTaskKeyTests"/>), which still runs first.
/// </summary>
public sealed class TaskCoordinatorResponseVariableKeyTests
{
    [Fact]
    public async Task ExecuteWithDetailsAsync_WithOptionsRefiner_AppliesDistinctResponseVariableKeyPerTask()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var firstTask = WorkflowTaskFactory.CreateHttpTask("first");
        var secondTask = WorkflowTaskFactory.CreateHttpTask("second");
        var firstDef = OnExecuteTask.Create(0, firstTask, ScriptCode.FromNative(string.Empty));
        var secondDef = OnExecuteTask.Create(0, secondTask, ScriptCode.FromNative(string.Empty));
        var definitions = new[] { firstDef, secondDef };

        // Mirrors how the (later) extension path would map its own OnExecuteTask instances back to
        // a per-extension key — by reference, since each extension owns a distinct OnExecuteTask
        // even when two extensions reference the identical underlying task.
        var keyByTask = new Dictionary<OnExecuteTask, string>
        {
            [firstDef] = "extensionA",
            [secondDef] = "extensionB"
        };

        var observedOptions = new ConcurrentDictionary<OnExecuteTask, TaskEngineExecutionOptions>();
        StubEngine(engine, observedOptions);

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);
        var context = CreateContext();

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions,
            Guid.NewGuid(),
            TaskTrigger.Extension,
            TaskExecutionOrigin.Extension,
            context,
            completedTaskIds: [],
            skipJournalProbe: false,
            optionsRefiner: (task, options) => options with { ResponseVariableKey = keyByTask[task] },
            cancellationToken: CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        observedOptions.Count.ShouldBe(2);
        observedOptions[firstDef].ResponseVariableKey.ShouldBe("extensionA");
        observedOptions[secondDef].ResponseVariableKey.ShouldBe("extensionB");
    }

    /// <summary>
    /// The refiner runs AFTER <c>ResolveGroupEngineOptions</c>, so the pre-existing duplicate-key
    /// journal suffixing (see <see cref="TaskCoordinatorDuplicateTaskKeyTests"/>) still fires
    /// alongside the refiner's own <see cref="TaskEngineExecutionOptions.ResponseVariableKey"/>
    /// override — the two disambiguators compose rather than compete.
    /// </summary>
    [Fact]
    public async Task ExecuteWithDetailsAsync_WithOptionsRefiner_DuplicateTaskKey_JournalKeyDisambiguationStillApplies()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var scriptTaskFirst = WorkflowTaskFactory.CreateHttpTask("script-task");
        var scriptTaskSecond = WorkflowTaskFactory.CreateHttpTask("script-task");
        var firstDef = OnExecuteTask.Create(0, scriptTaskFirst, ScriptCode.FromNative(string.Empty));
        var secondDef = OnExecuteTask.Create(0, scriptTaskSecond, ScriptCode.FromNative(string.Empty));
        var definitions = new[] { firstDef, secondDef };

        var observedOptions = new ConcurrentDictionary<OnExecuteTask, TaskEngineExecutionOptions>();
        StubEngine(engine, observedOptions);

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);
        var context = CreateContext();

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions,
            Guid.NewGuid(),
            TaskTrigger.Extension,
            TaskExecutionOrigin.Extension,
            context,
            completedTaskIds: [],
            skipJournalProbe: false,
            optionsRefiner: (_, options) => options with { ResponseVariableKey = "shared-extension-key" },
            cancellationToken: CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        // The refiner's override applies to both occurrences...
        observedOptions[firstDef].ResponseVariableKey.ShouldBe("shared-extension-key");
        observedOptions[secondDef].ResponseVariableKey.ShouldBe("shared-extension-key");
        // ...while the pre-existing duplicate-key JournalTaskKey suffixing is untouched by it.
        observedOptions[firstDef].JournalTaskKey.ShouldBe("script-task#0");
        observedOptions[secondDef].JournalTaskKey.ShouldBe("script-task#1");
    }

    [Fact]
    public async Task ExecuteWithDetailsAsync_NoOptionsRefiner_ResponseVariableKeyStaysNull()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var task = WorkflowTaskFactory.CreateHttpTask("plain-task");
        var def = OnExecuteTask.Create(0, task, ScriptCode.FromNative(string.Empty));
        var definitions = new[] { def };

        var observedOptions = new ConcurrentDictionary<OnExecuteTask, TaskEngineExecutionOptions>();
        StubEngine(engine, observedOptions);

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);
        var context = CreateContext();

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, context,
            completedTaskIds: []);

        result.IsSuccess.ShouldBeTrue();
        observedOptions[def].ResponseVariableKey.ShouldBeNull();
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

    private static TaskCoordinator CreateCoordinator(
        ITaskExecutionEngine engine,
        ServiceProvider services) => new(
            engine,
            services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IConditionEvaluator>(),
            Substitute.For<ITimerEvaluator>(),
            new ExecutionErrorFactory(new ErrorNormalizer()),
            NullLogger<TaskCoordinator>.Instance);

    private static ScriptContext CreateContext() =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .Build();
}
