using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Unit tests for the S8 crash-resume checkpoint in <see cref="TransitionExecutor"/>.
/// Validates that the last successfully completed step is recorded (and the chain heartbeat
/// refreshed) before Finalize, that a persisted checkpoint resumes the plan from the next
/// step, and that an explicit ResumeFrom directive takes precedence over the checkpoint.
/// </summary>
public class TransitionExecutorCheckpointTests
{
    [Fact]
    public async Task ExecuteOneAsync_ShouldCheckpointLastCompletedStepBeforeFinalize()
    {
        // Arrange — steps at 20/30/50 plus Finalize (110) and a post-finalize step (112)
        var steps = new[]
        {
            CreateStep(LifecycleOrder.CreateTransition),
            CreateStep(LifecycleOrder.OnExecute),
            CreateStep(LifecycleOrder.ChangeState),
            CreateStep(LifecycleOrder.Finalize),
            CreateStep(LifecycleOrder.Finalize + 2)
        };
        var executor = CreateExecutor(steps);
        var context = CreateContext();

        // Act
        var result = await executor.ExecuteOneAsync(context, CancellationToken.None);

        // Assert — checkpoint stops advancing at Finalize so it can never leak past the clear
        result.IsSuccess.ShouldBeTrue();
        context.Instance.ResumePointStepOrder.ShouldBe(LifecycleOrder.ChangeState);
    }

    [Fact]
    public async Task ExecuteOneAsync_ShouldResumeFromPersistedCheckpoint()
    {
        // Arrange — a prior run committed steps up to OnExecute (30)
        var executed = new List<int>();
        var steps = new[]
        {
            CreateStep(LifecycleOrder.CreateTransition, executed),
            CreateStep(LifecycleOrder.OnExecute, executed),
            CreateStep(LifecycleOrder.ChangeState, executed)
        };
        var executor = CreateExecutor(steps);
        var context = CreateContext();
        context.Instance.SetResumePoint(LifecycleOrder.OnExecute);

        // Act
        var result = await executor.ExecuteOneAsync(context, CancellationToken.None);

        // Assert — already-committed steps are not re-executed (no duplicate remote effects)
        result.IsSuccess.ShouldBeTrue();
        executed.ShouldBe(new[] { LifecycleOrder.ChangeState });
    }

    [Fact]
    public async Task ExecuteOneAsync_ExplicitResumeDirective_ShouldTakePrecedenceOverCheckpoint()
    {
        // Arrange — subflow/long-poll resumes set an explicit directive; the stale checkpoint
        // on the instance must not override it
        var executed = new List<int>();
        var steps = new[]
        {
            CreateStep(LifecycleOrder.CreateTransition, executed),
            CreateStep(LifecycleOrder.OnExecute, executed),
            CreateStep(LifecycleOrder.ChangeState, executed)
        };
        var executor = CreateExecutor(steps);
        var context = CreateContext();
        context.Instance.SetResumePoint(LifecycleOrder.CreateTransition);
        context.Directives.RequestResumeFrom(LifecycleOrder.ChangeState);

        // Act
        var result = await executor.ExecuteOneAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executed.ShouldBe(new[] { LifecycleOrder.ChangeState });
    }

    private static TransitionExecutor CreateExecutor(IEnumerable<ITransitionStep> steps) =>
        new(steps, Substitute.For<ILogger<TransitionExecutor>>());

    private static ITransitionStep CreateStep(int order, List<int>? executed = null)
    {
        var step = Substitute.For<ITransitionStep>();
        step.Order.Returns(order);
        step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                executed?.Add(order);
                return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
            });
        return step;
    }

    private static TransitionExecutionContext CreateContext(string transitionKey = "test-transition")
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateWorkflow(workflowKey, domain);
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
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow(string key, string domain)
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
