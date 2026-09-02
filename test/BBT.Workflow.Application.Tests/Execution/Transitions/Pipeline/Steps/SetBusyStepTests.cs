using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for SetBusyStep
/// Tests that instance is set to Busy status at the start of transition processing
/// </summary>
public class SetBusyStepTests
{
    private readonly IInstanceRepository _mockInstanceRepository;
    private readonly ILogger<SetBusyStep> _mockLogger;
    private readonly SetBusyStep _step;

    public SetBusyStepTests()
    {
        _mockInstanceRepository = Substitute.For<IInstanceRepository>();
        _mockLogger = Substitute.For<ILogger<SetBusyStep>>();
        _step = new SetBusyStep(_mockInstanceRepository, _mockLogger);
    }

    [Fact]
    public void Order_ShouldBeSetBusy()
    {
        // Assert
        _step.Order.ShouldBe(LifecycleOrder.SetBusy);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsActive_ShouldSetToBusy()
    {
        // Arrange — the aggregate-aware CAS mutates the instance in memory on success; the mock
        // mimics that contract.
        var context = CreateTransitionExecutionContext();
        context.Instance.IsActive.ShouldBeTrue();
        _mockInstanceRepository.TryMarkBusyAsync(context.Instance, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<Instance>().Busy();
                return true;
            });

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert — one set-based CAS, no tracked full-row save.
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        await _mockInstanceRepository.Received(1).TryMarkBusyAsync(context.Instance, Arg.Any<CancellationToken>());
        await _mockInstanceRepository.DidNotReceive()
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenAdmissionOwnsStatus_ShouldAlignInMemoryWithoutAnyDbCall()
    {
        // Admission (Reserve/TakeOver) flipped the ROW in its own RequiresNew DbContext, so this
        // pipeline's aggregate still reads Active. The step must align the in-memory aggregate —
        // settlement's owner guard reads context.Instance.IsBusy and would otherwise strand the
        // instance Busy — and must NOT touch the database (the row is already Busy).
        var context = CreateTransitionExecutionContext();
        context.OwnsStatus = true;
        context.Instance.IsActive.ShouldBeTrue();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        await _mockInstanceRepository.DidNotReceive()
            .TryMarkBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
        await _mockInstanceRepository.DidNotReceive()
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenCasLosesRace_ShouldContinueWithoutFlip()
    {
        // A lost CAS means the row is no longer Active (concurrent writer); the pipeline proceeds
        // without the flip instead of blindly overwriting the status like the old tracked save.
        var context = CreateTransitionExecutionContext();
        _mockInstanceRepository.TryMarkBusyAsync(context.Instance, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
        context.Instance.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsAlreadyBusy_ShouldSkipAndNotUpdate()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Instance.Busy(); // Set to Busy before test

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        await _mockInstanceRepository.DidNotReceive()
            .TryMarkBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsCompleted_ShouldSkipAndNotUpdate()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Instance.Complete("test-domain"); // Set to Completed before test

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsCompleted.ShouldBeTrue();
        await _mockInstanceRepository.DidNotReceive()
            .TryMarkBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpdateDataTransition_ShouldNeverMarkBusy()
    {
        // updateData is status-neutral. Marking an Active instance Busy here would strand it:
        // a non-owning execution is barred from ResolveAvailable and settlement, so nothing
        // would ever flip it back.
        var context = CreateTransitionExecutionContext("update-parent-data");
        context.Instance.IsActive.ShouldBeTrue();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsActive.ShouldBeTrue();
        await _mockInstanceRepository.DidNotReceive()
            .TryMarkBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenIsSubFlowResume_ShouldSkipAndNotUpdate()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Directives.MarkAsSubFlowResume();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsActive.ShouldBeTrue(); // Should remain Active
        await _mockInstanceRepository.DidNotReceive()
            .TryMarkBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnContinueOutcome()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    private TransitionExecutionContext CreateTransitionExecutionContext(
        string transitionKey = "test-transition")
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create(transitionKey, null, "state1", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private Definitions.Workflow CreateMockWorkflow(string key, string domain)
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           {
                               "key": "state1",
                               "stateType": "Intermediate",
                               "transitions": []
                           }
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

        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}