using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Continuations;

/// <summary>
/// Unit tests for <see cref="EnqueueContinuationStrategy"/> — verifies the direct Dapr-enqueue
/// mode (default), its outbox fallback on enqueue failure, and the legacy always-outbox mode.
/// </summary>
public class EnqueueContinuationStrategyTests
{
    private readonly Mock<IDistributedEventBus> _eventBus = new();
    private readonly Mock<IInstanceJobRepository> _jobRepository = new();
    private readonly Mock<ITransitionJobEnqueuer> _jobEnqueuer = new();
    private readonly Mock<ILogger<EnqueueContinuationStrategy>> _logger = new();

    public EnqueueContinuationStrategyTests()
    {
        _jobRepository
            .Setup(x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(It.IsAny<InstanceJob>());
    }

    private EnqueueContinuationStrategy CreateStrategy(bool directEnqueue) =>
        new(
            _eventBus.Object,
            _jobRepository.Object,
            _jobEnqueuer.Object,
            Options.Create(new WorkflowExecutionOptions { DirectEnqueueContinuations = directEnqueue }),
            _logger.Object);

    [Fact]
    public async Task DirectMode_WhenEnqueueSucceeds_EnqueuesDaprJobAndDoesNotWriteOutbox()
    {
        var strategy = CreateStrategy(directEnqueue: true);
        var context = CreateContextWithNextTransition("approve");

        var result = await strategy.DispatchAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull(); // ends the in-process loop

        _jobEnqueuer.Verify(
            x => x.EnqueueAsync(
                It.Is<TransitionJobPayload>(p =>
                    p.TransitionKey == "approve"
                    && p.InstanceId == context.InstanceId
                    && p.ExecutionActor == ExecutionActor.System),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _eventBus.Verify(
            x => x.PublishAsync(
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        _jobRepository.Verify(
            x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DirectMode_WhenEnqueueFails_FallsBackToOutbox()
    {
        var strategy = CreateStrategy(directEnqueue: true);
        var context = CreateContextWithNextTransition("approve");

        _jobEnqueuer
            .Setup(x => x.EnqueueAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dapr unavailable"));

        var result = await strategy.DispatchAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();

        _jobEnqueuer.Verify(
            x => x.EnqueueAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _eventBus.Verify(
            x => x.PublishAsync(
                It.Is<TransitionContinuationRequested>(e => e.TransitionKey == "approve"),
                It.IsAny<string?>(),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OutboxMode_AlwaysPublishesViaOutboxAndNeverEnqueuesDirectly()
    {
        var strategy = CreateStrategy(directEnqueue: false);
        var context = CreateContextWithNextTransition("approve");

        var result = await strategy.DispatchAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();

        _jobEnqueuer.Verify(
            x => x.EnqueueAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _eventBus.Verify(
            x => x.PublishAsync(
                It.Is<TransitionContinuationRequested>(e => e.TransitionKey == "approve"),
                It.IsAny<string?>(),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenNoNextTransition_ReturnsNullWithoutSideEffects()
    {
        var strategy = CreateStrategy(directEnqueue: true);
        var context = CreateContext(); // no next transition requested

        var result = await strategy.DispatchAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();

        _jobRepository.Verify(
            x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _jobEnqueuer.Verify(
            x => x.EnqueueAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _eventBus.Verify(
            x => x.PublishAsync(
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
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
