using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// vnext#1252: a hop that fails inside <see cref="CreateTransitionRecordStep"/> — its mapping, the instance key, or
/// the data write with its master-schema check — used to fault the instance before its transition record was saved,
/// so retry, which re-runs the latest open record, found nothing (<c>Instance:100028</c>). The step now saves the
/// record unfinished before the failure travels on, for every hop retry can re-run.
/// </summary>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public class CreateTransitionRecordStepFailedHopTests
{
    private static readonly System.Text.Json.JsonSerializerOptions DefinitionJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly Error MappingFailed =
        Error.Failure(WorkflowErrorCodes.ExecutionStepFailed, "Failed to execute transition mapping: no customer");

    private readonly IInstanceTransitionRepository _transitionRepository = Substitute.For<IInstanceTransitionRepository>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceDataWriteService _dataWriteService = Substitute.For<IInstanceDataWriteService>();
    private readonly ITransitionDataMapper _dataMapper = Substitute.For<ITransitionDataMapper>();
    private readonly CreateTransitionRecordStep _step;
    private readonly List<InstanceTransition> _inserted = new();

    public CreateTransitionRecordStepFailedHopTests()
    {
        _transitionRepository
            .InsertAsync(Arg.Do<InstanceTransition>(_inserted.Add), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<InstanceTransition>());

        _step = new CreateTransitionRecordStep(
            _transitionRepository,
            _instanceRepository,
            _dataWriteService,
            Substitute.For<IGuidGenerator>(),
            _dataMapper,
            BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
            Substitute.For<ILogger<CreateTransitionRecordStep>>());
    }

    [Fact]
    public async Task MappingFails_SavesTheHopsRecordUnfinished_AndReturnsTheFailure()
    {
        MapperAnswers(Result<object?>.Fail(MappingFailed));
        var context = CreateContext();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(MappingFailed.Code);
        await _transitionRepository.DidNotReceiveWithAnyArgs().ForgetAsync(default!);
        var record = _inserted.ShouldHaveSingleItem();
        record.InstanceId.ShouldBe(context.InstanceId);
        record.TransitionId.ShouldBe("approve");
        record.FromState.ShouldBe("state1");
        record.FinishedAt.ShouldBeNull("retry re-runs the latest record that has no finish time");
        record.Body.Json.ShouldContain("\"amount\":1");
        await _transitionRepository.Received(1).InsertAsync(record, true, Arg.Any<CancellationToken>());
        context.Items.ContainsKey("TransitionRecordId").ShouldBeFalse("the hop did not get past its record");
    }

    [Fact]
    public async Task DataWriteThrows_SavesTheRecord_AndRethrowsTheException()
    {
        MapperAnswers(Result<object?>.Ok(new { amount = 1, notInTheSchema = true }));
        var thrown = new InvalidOperationException("JSON schema validation failed");
        _dataWriteService
            .AppendAsync(Arg.Any<Instance>(), Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
                Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>(), Arg.Any<bool>())
            .ThrowsAsync(thrown);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => _step.ExecuteAsync(CreateContext(), CancellationToken.None));

        exception.ShouldBeSameAs(thrown);
        _inserted.ShouldHaveSingleItem().FinishedAt.ShouldBeNull();
    }

    [Fact]
    public async Task KeyAlreadyTaken_SavesTheRecord()
    {
        MapperAnswers(Result<object?>.Ok(null));
        _instanceRepository.AnyActiveByKeyAsync("taken", Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var context = CreateContext(instanceKey: "taken");

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        _inserted.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RetryThatFailsAgain_KeepsTheOriginalRecord_AndSavesNoSecondOne()
    {
        MapperAnswers(Result<object?>.Fail(MappingFailed));
        var context = CreateContext();
        var original = InstanceTransition.Create(
            Guid.NewGuid(), context.InstanceId, "approve", "state1", TriggerType.Manual,
            new JsonData("{}"), new JsonData("{}"));
        context.RetryOfTransitionRecordId = original.Id;
        _transitionRepository.FindAsync(original.Id, true, Arg.Any<CancellationToken>()).Returns(original);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        _inserted.ShouldBeEmpty();
        await _transitionRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default);
    }

    [Fact]
    public async Task HistoryNone_SavesNoRecord()
    {
        MapperAnswers(Result<object?>.Fail(MappingFailed));
        var context = CreateContext();
        context.Workflow.SetHistory(HistoryMode.None);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        _inserted.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TriggerType.Automatic)]
    [InlineData(TriggerType.Scheduled)]
    [InlineData(TriggerType.Event)]
    public async Task AHopRetryCannotReRun_SavesNoRecord(TriggerType trigger)
    {
        // Retry re-runs a hop as a manual request, which these refuse (vnext#1338): a record would only let a
        // refused retry unfault the instance first.
        MapperAnswers(Result<object?>.Fail(MappingFailed));

        var result = await _step.ExecuteAsync(CreateContext(trigger: trigger), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        _inserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ErrorBoundaryHop_SavesNoRecord()
    {
        MapperAnswers(Result<object?>.Fail(MappingFailed));

        var result = await _step.ExecuteAsync(CreateContext(errorBoundary: true), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        _inserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordWriteFails_TheHopsOwnFailureStillTravelsOn()
    {
        MapperAnswers(Result<object?>.Fail(MappingFailed));
        _transitionRepository
            .InsertAsync(Arg.Any<InstanceTransition>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("the database is gone"));

        var result = await _step.ExecuteAsync(CreateContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(MappingFailed.Code);
        // Left Added, the record would be inserted again by the unit of work's commit after the fault.
        await _transitionRepository.Received(1).ForgetAsync(Arg.Is<InstanceTransition>(t => t.TransitionId == "approve"));
    }

    [Fact]
    public async Task RecordWrite_RunsOnALiveTokenAfterTheCallerCancelled()
    {
        // The hop failed, often because the caller went away: the record is the fault path's own work.
        MapperAnswers(Result<object?>.Fail(MappingFailed));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await _step.ExecuteAsync(CreateContext(), cts.Token);

        await _transitionRepository.Received(1).InsertAsync(
            Arg.Any<InstanceTransition>(), true, Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task SuccessfulHop_InsertsItsRecordOnce()
    {
        MapperAnswers(Result<object?>.Ok(null));

        var result = await _step.ExecuteAsync(CreateContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _inserted.ShouldHaveSingleItem();
    }

    private void MapperAnswers(Result<object?> result) =>
        _dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<Action<Exception>?>(), Arg.Any<BBT.Workflow.Shared.ExecutionActor?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private static TransitionExecutionContext CreateContext(
        TriggerType trigger = TriggerType.Manual, bool errorBoundary = false, string? instanceKey = null)
    {
        var workflow = CreateWorkflow();
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        var state = workflow.GetState("state1").Value!;
        instance.ChangeState(state);

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "approve",
            Trigger = trigger,
            IsErrorBoundaryTransition = errorBoundary,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = Transition.Create("approve", "state1", "state2", trigger, "Patch"),
            Instance = instance,
            Data = new { amount = 1 },
            InstanceKey = instanceKey,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow()
    {
        const string json = """
            {
                "type": "F", "timeout": null, "labels": [], "functions": [], "features": [],
                "states": [
                    {"key": "state1", "stateType": "Intermediate", "transitions": [
                        {"key": "approve", "from": "state1", "target": "state2", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": []}
                    ]},
                    {"key": "state2", "stateType": "Intermediate", "transitions": []}
                ],
                "sharedTransitions": [], "extensions": [],
                "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
            }
            """;
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, DefinitionJson)!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
