using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Unit tests for <see cref="PipelineProfileResolver"/>.
/// </summary>
public sealed class PipelineProfileResolverTests
{
    private readonly PipelineProfileResolver _resolver = new();

    [Fact]
    public void Resolve_WhenIsErrorBoundaryTransitionTrue_ShouldReturnErrorBoundaryProfileRegardlessOfTrigger()
    {
        var context = new WorkflowExecutionContext
        {
            IsErrorBoundaryTransition = true,
            TriggerType = TriggerType.Automatic,
            Domain = "d",
            WorkflowKey = "w",
            TransitionKey = "t",
        };

        var profile = _resolver.Resolve(context);

        profile.ShouldBeSameAs(PipelineExecutionProfile.ForErrorBoundary());
        profile.Name.ShouldBe("ErrorBoundary");
    }

    [Fact]
    public void Resolve_WhenTriggerManual_ShouldReturnManualProfile()
    {
        var context = CreateContext(TriggerType.Manual, isErrorBoundary: false);
        var profile = _resolver.Resolve(context);
        profile.ShouldBeSameAs(PipelineExecutionProfile.ForManual());
        profile.Name.ShouldBe("Manual");
    }

    [Fact]
    public void Resolve_WhenTriggerAutomatic_ShouldReturnAutoChainProfile()
    {
        var context = CreateContext(TriggerType.Automatic, isErrorBoundary: false);
        var profile = _resolver.Resolve(context);
        profile.ShouldBeSameAs(PipelineExecutionProfile.ForAutoChain());
        profile.Name.ShouldBe("AutoChain");
    }

    [Fact]
    public void Resolve_WhenTriggerScheduled_ShouldReturnScheduledProfile()
    {
        var context = CreateContext(TriggerType.Scheduled, isErrorBoundary: false);
        var profile = _resolver.Resolve(context);
        profile.ShouldBeSameAs(PipelineExecutionProfile.ForScheduled());
        profile.Name.ShouldBe("Scheduled");
    }

    [Fact]
    public void Resolve_WhenTriggerEvent_ShouldReturnEventProfile()
    {
        var context = CreateContext(TriggerType.Event, isErrorBoundary: false);
        var profile = _resolver.Resolve(context);
        profile.ShouldBeSameAs(PipelineExecutionProfile.ForEvent());
        profile.Name.ShouldBe("Event");
    }

    [Fact]
    public void Resolve_WhenTriggerUnknown_ShouldFallbackToManualProfile()
    {
        var context = CreateContext((TriggerType)999, isErrorBoundary: false);
        var profile = _resolver.Resolve(context);
        profile.ShouldBeSameAs(PipelineExecutionProfile.ForManual());
        profile.Name.ShouldBe("Manual");
    }

    [Fact]
    public void Resolve_WhenContextNull_ShouldThrowArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => _resolver.Resolve(null!));
    }

    private static WorkflowExecutionContext CreateContext(TriggerType triggerType, bool isErrorBoundary) =>
        new()
        {
            Domain = "test-domain",
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowKey = "workflow",
            TransitionKey = "transition",
            TriggerType = triggerType,
            IsErrorBoundaryTransition = isErrorBoundary,
        };
}
