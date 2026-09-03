using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for the retry re-entry path of <see cref="CreateTransitionRecordStep"/>: a retry
/// carries the ORIGINAL transition record id (<c>RetryOfTransitionRecordId</c>) and the step must
/// reuse that record — the task journal is keyed by transition record id, so only a reused id
/// lets <c>GetSuccessfulTaskIdsAsync</c> bypass already-completed tasks instead of re-running
/// their side effects.
/// </summary>
public class CreateTransitionRecordStepRetryTests
{
    private readonly IInstanceTransitionRepository _transitionRepository =
        Substitute.For<IInstanceTransitionRepository>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceDataWriteService _dataWriteService = Substitute.For<IInstanceDataWriteService>();
    private readonly ITransitionDataMapper _dataMapper = Substitute.For<ITransitionDataMapper>();
    private readonly CreateTransitionRecordStep _step;

    public CreateTransitionRecordStepRetryTests()
    {
        _dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(), Arg.Any<IRuntimeInfoProvider>(),
                Arg.Any<System.Collections.Generic.Dictionary<string, string?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(null));

        _step = new CreateTransitionRecordStep(
            _transitionRepository,
            _instanceRepository,
            _dataWriteService,
            Substitute.For<IGuidGenerator>(),
            _dataMapper,
            Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<ILogger<CreateTransitionRecordStep>>());
    }

    [Fact]
    public async Task ExecuteAsync_RetryWithExistingRecord_ReusesItInsteadOfInserting()
    {
        var context = CreateContext();
        var original = InstanceTransition.Create(
            Guid.NewGuid(), context.InstanceId, context.TransitionKey,
            "state1", TriggerType.Manual, new JsonData("{}"), new JsonData("{}"));
        context.RetryOfTransitionRecordId = original.Id;
        _transitionRepository.FindAsync(original.Id, true, Arg.Any<CancellationToken>())
            .Returns(original);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Items["TransitionRecordId"].ShouldBe(original.Id);
        await _transitionRepository.DidNotReceive()
            .InsertAsync(Arg.Any<InstanceTransition>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _transitionRepository.Received(1)
            .UpdateAsync(original, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RetryWithMissingRecord_FallsBackToCreatingANewOne()
    {
        var context = CreateContext();
        context.RetryOfTransitionRecordId = Guid.NewGuid();
        _transitionRepository.FindAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((InstanceTransition?)null);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _transitionRepository.Received(1)
            .InsertAsync(Arg.Any<InstanceTransition>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutRetry_CreatesANewRecordAndNeverLooksUp()
    {
        var context = CreateContext();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _transitionRepository.Received(1)
            .InsertAsync(Arg.Any<InstanceTransition>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _transitionRepository.DidNotReceive()
            .FindAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PersistWork_IsOwnedByTransitionRecordPersistSpan()
    {
        var collected = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var root = new Activity("transition-record-persist-test").Start();

        var result = await _step.ExecuteAsync(CreateContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var span = collected.Single(a =>
            a.TraceId == root.TraceId && a.DisplayName == "TransitionRecord.Persist");
        span.ParentId.ShouldBe(root.Id);
        span.GetTagItem(TelemetryConstants.TagNames.SpanCategory)
            .ShouldBe(TelemetryConstants.SpanCategories.Business);
    }

    private static TransitionExecutionContext CreateContext()
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
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = Transition.Create("test-transition", null, "state1", TriggerType.Manual, "Patch"),
            Instance = instance,
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow()
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           {"key": "state1", "stateType": "Intermediate", "transitions": [
                               {"key": "test-transition", "from": "state1", "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": []}
                           ]}
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
