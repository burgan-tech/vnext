using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Coordinator;

using BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// The task journal's request/response/invocation payloads are serialized only when a journal row is
/// persisted, and the response's <c>Data</c> is serialized once (the materialized instance-data delta is
/// reused for the journal text). Neither changes what the task returns or what it appends.
/// </summary>
public sealed class TaskExecutionEngineJournalPayloadTests
{
    private readonly ITaskExecutorRegistry _executorRegistry = Substitute.For<ITaskExecutorRegistry>();
    private readonly ITaskFactory _taskFactory = Substitute.For<ITaskFactory>();
    private readonly ITaskPersistenceStrategyFactory _strategyFactory = Substitute.For<ITaskPersistenceStrategyFactory>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly IInstanceDataWriteService _writeService = Substitute.For<IInstanceDataWriteService>();
    private readonly List<string> _appendedDeltas = [];

    public TaskExecutionEngineJournalPayloadTests()
    {
        _guidGenerator.Create().Returns(_ => Guid.NewGuid());
        _writeService.AppendAsync(Arg.Any<Instance>(), Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
                Arg.Any<CancellationToken>(), Arg.Any<BBT.Workflow.Definitions.Workflow?>(), Arg.Any<bool>())
            .Returns(call =>
            {
                _appendedDeltas.Add(call.ArgAt<JsonData>(1).Json);
                return Task.FromResult<InstanceData?>(null);
            });
    }

    [Fact]
    public async Task WithoutPersistenceStrategy_SkipsJournalPayloads_ButTaskResultAndDataAreUnchanged()
    {
        _strategyFactory.GetStrategy(Arg.Any<TaskExecutionOrigin>())
            .Returns(Result<ITaskPersistenceStrategy>.Fail(new Error("none", "no journal")));
        ArrangeExecutor(out var seenContexts);

        var withoutJournal = await RunAsync();

        withoutJournal.IsSuccess.ShouldBeTrue();
        withoutJournal.Value!.IsSuccess.ShouldBeTrue();
        seenContexts.ShouldHaveSingleItem().CapturesJournalPayloads.ShouldBeFalse();
        _appendedDeltas.ShouldHaveSingleItem().ShouldBe(ExpectedDataJson());
    }

    [Fact]
    public async Task WithPersistenceStrategy_JournalTextIsExactlyTheFullResponseSerialization()
    {
        var strategy = Substitute.For<ITaskPersistenceStrategy>();
        strategy.HandleCreationAsync(Arg.Any<InstanceTask>(), Arg.Any<TaskTrigger>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<InstanceTask>());
        InstanceTask? completed = null;
        strategy.HandleCompletionAsync(Arg.Any<InstanceTask>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                completed = call.Arg<InstanceTask>();
                return Task.CompletedTask;
            });
        _strategyFactory.GetStrategy(Arg.Any<TaskExecutionOrigin>())
            .Returns(Result<ITaskPersistenceStrategy>.Ok(strategy));
        ArrangeExecutor(out var seenContexts);

        var result = await RunAsync();

        result.IsSuccess.ShouldBeTrue();
        seenContexts.ShouldHaveSingleItem().CapturesJournalPayloads.ShouldBeTrue();
        _appendedDeltas.ShouldHaveSingleItem().ShouldBe(ExpectedDataJson());
        completed.ShouldNotBeNull();
        completed.Response.Json.ShouldBe(JsonSerializer.Serialize(NewResponse(), JsonSerializerConstants.JsonOptions));
        completed.Request.Json.ShouldContain("mock-api");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SerializeJournalResponse_ReusingTheDelta_IsByteIdenticalAndRestoresData(bool withDelta)
    {
        var response = NewResponse();
        var expected = JsonSerializer.Serialize(response, JsonSerializerConstants.JsonOptions);
        object originalData = response.Data!;
        var delta = withDelta ? JsonData.FromMaterializedObject(originalData, JsonSerializerConstants.JsonOptions) : null;

        var journal = TaskExecutionEngine.SerializeJournalResponse(response, delta);

        journal.Json.ShouldBe(expected);
        ((object)response.Data!).ShouldBeSameAs(originalData);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static StandardTaskResponse NewResponse()
    {
        dynamic data = new ExpandoObject();
        data.id = 42;
        data.amount = 12.50m;
        data.name = "çağrı \"quoted\" <tag> & ü";
        data.nested = new Dictionary<string, object?> { ["a"] = new[] { 1, 2, 3 }, ["b"] = null, ["c"] = true };
        return new StandardTaskResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Data = data,
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            Metadata = new Dictionary<string, object> { ["Url"] = "https://x.local" },
            ExecutionDurationMs = 7,
            TaskType = "Http"
        };
    }

    private static string ExpectedDataJson() =>
        JsonData.FromMaterializedObject((object)NewResponse().Data!, JsonSerializerConstants.JsonOptions).Json;

    private ITaskExecutor ArrangeExecutor(out List<TaskExecutorContext> seenContexts)
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(task));

        var contexts = new List<TaskExecutorContext>();
        seenContexts = contexts;
        var executor = Substitute.For<ITaskExecutor>();
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                contexts.Add(call.Arg<TaskExecutorContext>());
                return Result<StandardTaskResponse>.Ok(NewResponse());
            });
        _executorRegistry.GetExecutor(Arg.Any<TaskType>()).Returns(Result<ITaskExecutor>.Ok(executor));
        return executor;
    }

    private Task<Result<TasksExecutionResult>> RunAsync()
    {
        var engine = new TaskExecutionEngine(
            _executorRegistry, _taskFactory, _strategyFactory, _guidGenerator,
            new ErrorBoundaryResolver(NullLogger<ErrorBoundaryResolver>.Instance),
            new ErrorActionExecutor(NullLogger<ErrorActionExecutor>.Instance),
            new ExecutionErrorFactory(new ErrorNormalizer()),
            _writeService,
            NullLogger<TaskExecutionEngine>.Instance);

        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
            .Build();

        return engine.ExecuteAsync(
            OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty)),
            Guid.NewGuid(),
            TaskTrigger.OnExecute,
            TaskExecutionOrigin.Flow,
            scriptContext,
            CancellationToken.None);
    }
}
