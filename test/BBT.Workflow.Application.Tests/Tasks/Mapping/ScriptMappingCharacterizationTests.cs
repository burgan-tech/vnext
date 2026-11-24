using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Resilience;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Characterization of the script (Roslyn <see cref="IMapping"/>) mapping path of three representative executors —
/// Http, DaprService and DirectTrigger — written BEFORE the executors' copied mapping blocks moved into one runner,
/// and run unchanged after every executor move. It pins what the legacy block produced: the input handler's
/// response, the output handler's data, one compile per execution (the memo), the exact failure codes and messages
/// (<c>"{label} handler failed: " + ScriptDiagnostics.Explain(ex)</c>), the WorkflowLogs event raised on the
/// executor's own logger, and whether the task response is filed on the script context with and without a mapping.
/// </summary>
public sealed class ScriptMappingCharacterizationTests
{
    public enum Kind { Http, DaprService, DirectTrigger }

    private const int TaskInputHandlerFailedEventId = 10072;
    private const int TaskOutputHandlerFailedEventId = 10073;

    /// <summary>The <see cref="IMapping"/> double the mocked engine hands out; each instance counts its calls.</summary>
    private sealed class MockMapping(Exception? inputFailure = null, Exception? outputFailure = null) : IMapping
    {
        public int InputCalls { get; private set; }

        public WorkflowTask? InputTask { get; private set; }

        public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
        {
            InputCalls++;
            InputTask = task;
            if (inputFailure is not null)
                throw inputFailure;
            return Task.FromResult(new ScriptResponse { Key = "in-key", Data = "mapped-input" });
        }

        public Task<ScriptResponse> OutputHandler(ScriptContext context)
        {
            if (outputFailure is not null)
                throw outputFailure;
            return Task.FromResult(new ScriptResponse { Data = new Dictionary<string, object?> { ["mapped"] = "output" } });
        }
    }

    [Theory]
    [InlineData(Kind.Http)]
    [InlineData(Kind.DaprService)]
    [InlineData(Kind.DirectTrigger)]
    public async Task ScriptRunner_SameOutputAsLegacyExecutorBlock(Kind kind)
    {
        var mapping = new MockMapping();
        var engine = EngineReturning(() => mapping);
        var harness = Harness.Create(kind, engine, withMapping: true);

        var input = await harness.PrepareInputAsync();
        input.IsSuccess.ShouldBeTrue();
        ((string)input.Value!.Data!).ShouldBe("mapped-input");
        input.Value.Key.ShouldBe("in-key");
        mapping.InputTask.ShouldBeSameAs(harness.Task);

        var output = await harness.ProcessOutputAsync(TaskInvocationResult.Success(data: "raw", statusCode: 200, taskType: "x"));
        output.IsSuccess.ShouldBeTrue();
        ((Dictionary<string, object?>)output.Value!)["mapped"].ShouldBe("output");

        // Both phases share one compile per execution (the memo), each phase gets a fresh instance.
        await engine.Received(1).CompileToFactoryAsync<IMapping>(
            Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings?>(), Arg.Any<CancellationToken>());

        // A mapped output always files the task response on the script context.
        harness.Context.ScriptContext.TaskResponse.ShouldContainKey(harness.Task.Key.ToVariableName());
        harness.Logger.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Kind.Http, "Http task input handler failed: ")]
    [InlineData(Kind.DaprService, "DaprService task input handler failed: ")]
    [InlineData(Kind.DirectTrigger, "Input handler failed for DirectTrigger: ")]
    public async Task ScriptRunner_InputFailure_SameCodeMessageAndLogAsLegacy(Kind kind, string prefix)
    {
        var boom = new InvalidOperationException("input boom");
        var harness = Harness.Create(kind, EngineReturning(() => new MockMapping(inputFailure: boom)), withMapping: true);

        var input = await harness.PrepareInputAsync();

        input.IsSuccess.ShouldBeFalse();
        input.Error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
        input.Error.Prefix.ShouldBe(Error.Failure("x").Prefix);
        input.Error.Message.ShouldBe(prefix + ScriptDiagnostics.Explain(boom));
        var logged = harness.Logger.Events.ShouldHaveSingleItem();
        logged.Id.ShouldBe(TaskInputHandlerFailedEventId);
        logged.Level.ShouldBe(LogLevel.Error);
        logged.Message.ShouldContain($"TaskType={kind}");
        logged.Message.ShouldContain(input.Error.Message!);
    }

    [Theory]
    [InlineData(Kind.Http, "Http task output handler failed: ")]
    [InlineData(Kind.DaprService, "DaprService task output handler failed: ")]
    [InlineData(Kind.DirectTrigger, "Output handler failed for DirectTrigger: ")]
    public async Task ScriptRunner_OutputFailure_SameCodeMessageAndLogAsLegacy(Kind kind, string prefix)
    {
        var boom = new ArgumentException("output boom");
        var harness = Harness.Create(kind, EngineReturning(() => new MockMapping(outputFailure: boom)), withMapping: true);

        var output = await harness.ProcessOutputAsync(TaskInvocationResult.Success(data: "raw"));

        output.IsSuccess.ShouldBeFalse();
        output.Error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
        output.Error.Message.ShouldBe(prefix + ScriptDiagnostics.Explain(boom));
        var logged = harness.Logger.Events.ShouldHaveSingleItem();
        logged.Id.ShouldBe(TaskOutputHandlerFailedEventId);
        logged.Level.ShouldBe(LogLevel.Error);
    }

    [Theory]
    [InlineData(Kind.Http, true)]
    [InlineData(Kind.DaprService, false)]
    [InlineData(Kind.DirectTrigger, true)]
    public async Task NoMapping_PassesThrough_AndFilesTheResponseOnlyWhereLegacyDid(Kind kind, bool filesResponse)
    {
        var engine = Substitute.For<IScriptEngine>();
        var harness = Harness.Create(kind, engine, withMapping: false);

        var input = await harness.PrepareInputAsync();
        input.IsSuccess.ShouldBeTrue();
        input.Value.ShouldBeNull();

        var output = await harness.ProcessOutputAsync(TaskInvocationResult.Success(data: "raw"));
        output.IsSuccess.ShouldBeTrue();
        output.Value.ShouldBe("raw");

        harness.Context.ScriptContext.TaskResponse.ContainsKey(harness.Task.Key.ToVariableName()).ShouldBe(filesResponse);
        await engine.DidNotReceiveWithAnyArgs().CompileToFactoryAsync<IMapping>(default!, default, default);
    }

    private static IScriptEngine EngineReturning(Func<IMapping> factory)
    {
        var engine = Substitute.For<IScriptEngine>();
        engine.CompileToFactoryAsync<IMapping>(Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(factory));
        return engine;
    }

    /// <summary>One executor plus its context; drives the protected phases through reflection so the same test runs
    /// against the executor's own override (before the refactor) and the base template (after).</summary>
    private sealed class Harness
    {
        private object Executor { get; init; } = null!;

        public WorkflowTask Task { get; private init; } = null!;

        public TaskExecutorContext Context { get; private init; } = null!;

        public CapturingLogger Logger { get; private init; } = null!;

        public static Harness Create(Kind kind, IScriptEngine engine, bool withMapping)
        {
            var remoteInvoker = Substitute.For<IRemoteInvokerService>();
            var runtime = Substitute.For<IRuntimeInfoProvider>();
            runtime.Domain.Returns("test-domain");
            var logger = new CapturingLogger();

            WorkflowTask task;
            object executor;
            switch (kind)
            {
                case Kind.Http:
                    task = HttpTask.Create("""{ "url": "https://example.test/x", "method": "POST" }""".ToJsonElement());
                    executor = new HttpTaskExecutor(remoteInvoker, engine, Substitute.For<ITaskInvocationDispatcher>(),
                        logger.For<HttpTaskExecutor>());
                    break;
                case Kind.DaprService:
                    task = DaprServiceTask.Create("""{ "appId": "a", "methodName": "/m", "httpVerb": "GET" }""".ToJsonElement());
                    executor = new DaprServiceTaskExecutor(remoteInvoker, engine, Substitute.For<ITaskInvocationDispatcher>(),
                        logger.For<DaprServiceTaskExecutor>());
                    break;
                default:
                    task = DirectTriggerTask.Create(
                        $$"""{ "domain": "test-domain", "flow": "order", "transitionName": "approve", "key": "{{Guid.NewGuid()}}" }""".ToJsonElement());
                    executor = new DirectTriggerTaskExecutor(engine, runtime, remoteInvoker,
                        Substitute.For<IInstanceCommandGateway>(), Substitute.For<IInstanceIdResolver>(),
                        Substitute.For<IDomainDiscoveryResolver>(), Substitute.For<IResultResiliencePipelineFactory>(),
                        new BBT.Aether.Users.CurrentUser(AsyncLocalCurrentUserAccessor.Instance), logger.For<DirectTriggerTaskExecutor>());
                    break;
            }

            task.SetReference(new Reference("char-task", "test-domain", "sys-tasks", "1.0.0"));
            var onExecute = OnExecuteTask.Create(1, task,
                withMapping ? ScriptCode.FromNative("return null;") : ScriptCode.FromNative(string.Empty));
            var scriptContext = new ScriptContext.Builder(Microsoft.Extensions.Logging.Abstractions.NullLogger<ScriptContext>.Instance)
                .SetRuntime(runtime)
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
                .Build();

            return new Harness
            {
                Executor = executor,
                Task = task,
                Logger = logger,
                Context = new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute,
                    TaskExecutionOrigin.Flow)
            };
        }

        public Task<Result<ScriptResponse?>> PrepareInputAsync() =>
            (Task<Result<ScriptResponse?>>)Method("PrepareInputAsync")
                .Invoke(Executor, [Task, Context, CancellationToken.None])!;

        public Task<Result<object?>> ProcessOutputAsync(TaskInvocationResult result) =>
            (Task<Result<object?>>)Method("ProcessOutputAsync")
                .Invoke(Executor, [Task, result, Context, CancellationToken.None])!;

        private MethodInfo Method(string name) =>
            Executor.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{Executor.GetType().Name} has no {name}.");
    }
}

/// <summary>Records every log event (id, level, formatted message) of the loggers it hands out.</summary>
internal sealed class CapturingLogger
{
    public List<(int Id, LogLevel Level, string Message)> Events { get; } = [];

    public ILogger<T> For<T>() => new Typed<T>(this);

    private sealed class Typed<T>(CapturingLogger owner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Events.Add((eventId.Id, logLevel, formatter(state, exception)));
    }
}
