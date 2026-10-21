using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

public class VersionRegistryInvalidationStepTests
{
    private readonly VersionRegistryInvalidationStep _step = new();

    [Fact]
    public void Order_ShouldSitJustBeforeFinalize()
    {
        _step.Order.ShouldBe(LifecycleOrder.Finalize - 1);
    }

    [Fact]
    public async Task Step_ShouldEnqueueJob_WhenWorkflowIsVersionRegistry()
    {
        var context = CreateContext(RuntimeSysSchemaInfo.VersionRegistry, "customer-onboarding", "set-default");

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Directives.PostCommitJobs.OfType<InvalidateVersionPolicyJob>().ShouldHaveSingleItem()
            .ShouldBe(new InvalidateVersionPolicyJob("core", "customer-onboarding"));
    }

    [Fact]
    public async Task Step_ShouldDoNothing_ForOtherWorkflows()
    {
        var context = CreateContext("customer-onboarding", "abc", "submit");

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Directives.PostCommitJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Step_ShouldEnqueue_OnStartTransition()
    {
        var context = CreateContext(RuntimeSysSchemaInfo.VersionRegistry, "customer-onboarding", "create",
            TriggerType.Manual, isStart: true);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Directives.PostCommitJobs.OfType<InvalidateVersionPolicyJob>().ShouldHaveSingleItem();
    }

    private static TransitionExecutionContext CreateContext(
        string workflowKey, string instanceKey, string transitionKey,
        TriggerType trigger = TriggerType.Manual, bool isStart = false)
    {
        var instance = InstanceFactory.CreateDefault();
        instance.SetKey(instanceKey);
        var workflow = Definitions.Workflow.Create();
        var transition = Transition.Create(transitionKey, "draft", "active", trigger, "Patch");
        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "core",
            WorkflowKey = workflowKey,
            TransitionKey = transition.Key,
            Trigger = trigger,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = StateFactory.CreateDefault("draft"),
            Target = StateFactory.CreateDefault("active"),
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
