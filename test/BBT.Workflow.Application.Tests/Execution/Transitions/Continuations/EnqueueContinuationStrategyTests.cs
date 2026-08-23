using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Continuations;

/// <summary>
/// Unit tests for <see cref="EnqueueContinuationStrategy"/> — the AutoTransitionMode.Scheduled
/// path. Verifies that the strategy persists a durable job intent, hands delivery to
/// <see cref="ITransitionEnqueueGateway"/>, and PROPAGATES an enqueue failure: with no outbox
/// fallback left behind the gateway, swallowing one would commit an intent nothing ever arms.
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
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Ok(null));
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
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenNextTransitionExists_ShouldPopulatePayloadCorrectly()
    {
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        TransitionJobPayload? capturedPayload = null;
        Guid capturedJobId = Guid.Empty;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, Guid, bool, CancellationToken>(
                (payload, jobId, _, _) => { capturedPayload = payload; capturedJobId = jobId; })
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Ok(null));

        await strategy.DispatchAsync(context, CancellationToken.None);

        capturedPayload.ShouldNotBeNull();
        capturedPayload!.TransitionKey.ShouldBe("approve");
        capturedPayload.InstanceId.ShouldBe(context.InstanceId);
        capturedPayload.ExecutionActor.ShouldBe(ExecutionActor.System);
        capturedPayload.CallerSync.ShouldBeFalse();

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

        Guid capturedJobId = Guid.Empty;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, Guid, bool, CancellationToken>(
                (_, jobId, _, _) => capturedJobId = jobId)
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Ok(null));

        await strategy.DispatchAsync(context, CancellationToken.None);

        insertedJob.ShouldNotBeNull();
        // InstanceJob.Id == JobId == the id handed to the gateway, so cancellation-by-id works.
        insertedJob!.Id.ShouldBe(insertedJob.JobId);
        insertedJob.JobId.ShouldBe(capturedJobId);
    }

    [Fact]
    public async Task WhenActivityIsAmbient_PayloadCarriesTheTraceContext()
    {
        // The hop crosses the scheduler, where nothing is ambient: whatever the payload does not
        // carry, the next hop cannot correlate to.
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        TransitionJobPayload? capturedPayload = null;
        Guid capturedJobId = Guid.Empty;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, Guid, bool, CancellationToken>(
                (payload, jobId, _, _) => { capturedPayload = payload; capturedJobId = jobId; })
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Ok(null));

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
        capturedPayload!.TraceParent.ShouldNotBeNullOrEmpty();
        capturedPayload.TraceState.ShouldBe("vendor=state");
    }

    [Fact]
    public async Task ChainedContinuation_CarriesBusinessCorrelation()
    {
        // The business correlation id must survive the async hop so every leg of an auto-chain
        // keeps one correlation.id.
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        TransitionJobPayload? capturedPayload = null;
        Guid capturedJobId = Guid.Empty;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, Guid, bool, CancellationToken>(
                (payload, jobId, _, _) => { capturedPayload = payload; capturedJobId = jobId; })
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Ok(null));

        await strategy.DispatchAsync(context, CancellationToken.None);

        capturedPayload.ShouldNotBeNull();
        capturedPayload!.CorrelationId.ShouldBe(context.CorrelationId);
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
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The behaviour change that came with dropping the outbox fallback. The durable intent is
    /// already written into the ambient unit of work by this point, so a swallowed enqueue failure
    /// would commit an intent nothing ever arms and leave the instance parked in Busy with no
    /// owner. Failing instead routes the pipeline into MarkInstanceFaultedAsync: visible, retryable.
    /// </summary>
    [Fact]
    public async Task WhenEnqueueFails_ShouldPropagateTheFailure()
    {
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Fail(
                Error.Dependency("Dependency", "scheduler unreachable", "Dapr")));

        var result = await strategy.DispatchAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldBe("scheduler unreachable");
    }

    /// <summary>
    /// The chained continuation arms inline: Aether already defers arming to the ambient unit of
    /// work's post-commit hook, and unlike the accept path this strategy holds no status lock, so
    /// there is nothing to move out of a critical section.
    /// </summary>
    [Fact]
    public async Task ChainedContinuation_ShouldNotDeferArming()
    {
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        var deferArming = true;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, Guid, bool, CancellationToken>(
                (_, _, defer, _) => deferArming = defer)
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Ok(null));

        await strategy.DispatchAsync(context, CancellationToken.None);

        deferArming.ShouldBeFalse();
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

    [Fact]
    public async Task WhenALaneIsEstablished_ShouldStampAnchorAsTraceRootAndCurrentSpanAsPredecessor()
    {
        // The whole point of the flat-lane work: the anchor and the predecessor are DIFFERENT
        // values. TraceRoot (anchor) becomes the next hop's PARENT, so hop N+1 is a sibling of
        // hop N; TraceParent (predecessor) is only linked. Before this, TraceParent was the parent
        // and nesting depth grew with every chained hop.
        const string laneAnchor = "00-cccccccccccccccccccccccccccccccc-cccccccccccccccc-01";

        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        TransitionJobPayload? capturedPayload = null;
        Guid capturedJobId = Guid.Empty;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, Guid, bool, CancellationToken>(
                (p, _, _, _) => capturedPayload = p)
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Ok(null));

        var ambient = new Activity("transition/previous-hop");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.Start();
        try
        {
            using (WorkflowTraceLane.Use(laneAnchor, parentAnchor: null, seq: 3))
            {
                await strategy.DispatchAsync(context, CancellationToken.None);
            }
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }

        capturedPayload.ShouldNotBeNull();
        capturedPayload!.TraceRoot.ShouldBe(laneAnchor);
        capturedPayload.TraceParent.ShouldBe(ambient.Id);
        capturedPayload.TraceRoot.ShouldNotBe(capturedPayload.TraceParent);
        capturedPayload.LaneSeq.ShouldBe(4);
    }

    [Fact]
    public async Task WhenNoLaneIsEstablished_ShouldLeaveTraceRootNullSoTheHopKeepsLegacyNesting()
    {
        var strategy = CreateStrategy();
        var context = CreateContextWithNextTransition("approve");

        TransitionJobPayload? capturedPayload = null;
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, Guid, bool, CancellationToken>(
                (p, _, _, _) => capturedPayload = p)
            .ReturnsAsync(Result<IBackgroundJobArmHandle?>.Ok(null));

        await strategy.DispatchAsync(context, CancellationToken.None);

        capturedPayload.ShouldNotBeNull();
        capturedPayload!.TraceRoot.ShouldBeNull();
    }
}
