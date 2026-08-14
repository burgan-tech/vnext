using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Continuations;

/// <summary>
/// Unit tests for <see cref="EnqueueContinuationStrategy"/>.
/// Verifies that the strategy persists a durable job intent and delegates enqueue
/// to <see cref="ITransitionEnqueueGateway"/>; mode decisions (direct vs outbox)
/// are encapsulated in the gateway and are not tested here.
/// </summary>
public class EnqueueContinuationStrategyTests
{
    private readonly Mock<IInstanceJobRepository> _jobRepository = new();
    private readonly Mock<ITransitionEnqueueGateway> _mockEnqueueGateway = new();

    public EnqueueContinuationStrategyTests()
    {
        _jobRepository
            .Setup(x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstanceJob j, bool _, CancellationToken _) => j);

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private EnqueueContinuationStrategy CreateStrategy() =>
        new(_jobRepository.Object, _mockEnqueueGateway.Object);

    [Fact]
    public async Task WhenNextTransitionExists_ShouldInsertJobAndCallEnqueueGateway()
    {
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        var result = await strategy.DispatchAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull(); // ends the in-process loop

        _jobRepository.Verify(
            x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenNextTransitionExists_ShouldPopulatePayloadCorrectly()
    {
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        TransitionJobPayload? capturedPayload = null;
        TransitionContinuationRequested? capturedEvent = null;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, TransitionContinuationRequested, CancellationToken>(
                (payload, evt, _) => { capturedPayload = payload; capturedEvent = evt; })
            .Returns(Task.CompletedTask);

        await strategy.DispatchAsync(context, CancellationToken.None);

        capturedPayload.ShouldNotBeNull();
        capturedPayload!.TransitionKey.ShouldBe("approve");
        capturedPayload.InstanceId.ShouldBe(context.InstanceId);
        capturedPayload.ExecutionActor.ShouldBe(ExecutionActor.System);
        capturedPayload.CallerSync.ShouldBeFalse();

        capturedEvent.ShouldNotBeNull();
        capturedEvent!.TransitionKey.ShouldBe("approve");
        capturedEvent.InstanceId.ShouldBe(context.InstanceId);
    }

    [Fact]
    public async Task WhenNextTransitionExists_JobIdShouldMatchBetweenIntentAndPayload()
    {
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        InstanceJob? insertedJob = null;
        _jobRepository
            .Setup(x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<InstanceJob, bool, CancellationToken>((j, _, _) => insertedJob = j)
            .ReturnsAsync((InstanceJob j, bool _, CancellationToken _) => j);

        TransitionContinuationRequested? capturedEvent = null;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, TransitionContinuationRequested, CancellationToken>(
                (_, evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        await strategy.DispatchAsync(context, CancellationToken.None);

        insertedJob.ShouldNotBeNull();
        capturedEvent.ShouldNotBeNull();
        // InstanceJob.Id == JobId == the id carried in the outbox event for cancellation-by-id to work
        insertedJob!.Id.ShouldBe(insertedJob.JobId);
        insertedJob.JobId.ShouldBe(capturedEvent!.JobId);
    }

    [Fact]
    public async Task WhenActivityIsAmbient_OutboxEventCarriesSameTraceContextAsDirectPayload()
    {
        // Regression: the direct payload got TraceParent/TraceState but the outbox event did not,
        // so continuations routed through the outbox fallback lost the trace entirely.
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        TransitionJobPayload? capturedPayload = null;
        TransitionContinuationRequested? capturedEvent = null;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, TransitionContinuationRequested, CancellationToken>(
                (payload, evt, _) => { capturedPayload = payload; capturedEvent = evt; })
            .Returns(Task.CompletedTask);

        var activity = new System.Diagnostics.Activity("pipeline");
        activity.SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C);
        activity.TraceStateString = "vendor=state";
        activity.Start();
        try
        {
            await strategy.DispatchAsync(context, CancellationToken.None);
        }
        finally
        {
            activity.Stop();
            System.Diagnostics.Activity.Current = null;
        }

        capturedPayload.ShouldNotBeNull();
        capturedEvent.ShouldNotBeNull();
        capturedPayload!.TraceParent.ShouldNotBeNullOrEmpty();
        capturedEvent!.TraceParent.ShouldBe(capturedPayload.TraceParent);
        capturedEvent.TraceState.ShouldBe(capturedPayload.TraceState);
    }

    [Fact]
    public async Task WhenNoNextTransition_ReturnsNullWithoutSideEffects()
    {
        var strategy = CreateStrategy();
        var context = CreateContext(); // no next transition requested

        var result = await strategy.DispatchAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();

        _jobRepository.Verify(
            x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static TransitionExecutionContext CreateContextWithNextTransition(string nextTransitionKey)
    {
        var context = CreateContext();
        context.Directives.RequestNextTransition(new NextTransitionRequest(nextTransitionKey));
        return context;
    }

    private static TransitionExecutionContext CreateContext()
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create("test-transition", null, "state1", TriggerType.Automatic, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "test-transition",
            Trigger = TriggerType.Automatic,
            Actor = ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = transition,
            Instance = instance,
            Data = new { test = "data" },
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateMockWorkflow(string key, string domain)
    {
        const string json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [
                {
                    "key": "state1",
                    "type": "P",
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
