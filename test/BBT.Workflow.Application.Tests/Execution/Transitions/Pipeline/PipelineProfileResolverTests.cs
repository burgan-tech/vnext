using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
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

    [Theory]
    [InlineData(TriggerType.Manual, "Manual+Self")]
    [InlineData(TriggerType.Automatic, "AutoChain+Self")]
    [InlineData(TriggerType.Scheduled, "Scheduled+Self")]
    [InlineData(TriggerType.Event, "Event+Self")]
    public void Resolve_WhenUpdateDataTargetsSelf_ShouldComposeSelfVariantOfTheTriggerProfile(
        TriggerType triggerType,
        string expectedName)
    {
        var context = CreateContext(triggerType, isErrorBoundary: false);
        var transitionContext = CreateTransitionContext(
            triggerType, WellKnownStateKeys.Self, WellKnownTransitionKeys.UpdateData);

        var profile = _resolver.Resolve(context, transitionContext);

        profile.Name.ShouldBe(expectedName);
        profile.ExcludedStepOrders.ShouldContain(LifecycleOrder.OnEntry);
        profile.ExcludedStepOrders.ShouldContain(LifecycleOrder.OnExit);
        profile.ExcludedStepOrders.ShouldContain(LifecycleOrder.CancelScheduledJobs);
        profile.ExcludedStepOrders.ShouldContain(LifecycleOrder.Schedule);
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.ChangeState);
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.Auto);
    }

    [Fact]
    public void Resolve_WhenTransitionTargetsAnotherState_ShouldReturnTheBaseProfileUnchanged()
    {
        var context = CreateContext(TriggerType.Manual, isErrorBoundary: false);
        var transitionContext = CreateTransitionContext(TriggerType.Manual, "another-state");

        var profile = _resolver.Resolve(context, transitionContext);

        profile.ShouldBeSameAs(PipelineExecutionProfile.ForManual());
    }

    /// <summary>
    /// A literal target equal to the current state must NOT compose the self variant: the start
    /// transition and a retry-after-commit both present that same shape while genuinely needing the
    /// state entered.
    /// </summary>
    [Fact]
    public void Resolve_WhenTargetLiterallyEqualsCurrentState_ShouldReturnTheBaseProfileUnchanged()
    {
        var context = CreateContext(TriggerType.Manual, isErrorBoundary: false);
        var transitionContext = CreateTransitionContext(TriggerType.Manual, CurrentStateKey);

        var profile = _resolver.Resolve(context, transitionContext);

        profile.ShouldBeSameAs(PipelineExecutionProfile.ForManual());
    }

    /// <summary>
    /// The self variant belongs to <c>updateData</c> alone. Any OTHER transition declaring
    /// <c>target: $self</c> — a shared transition being the real case — keeps the trigger's base
    /// profile and therefore runs the state's full lifecycle. "Do not move the instance" is not the
    /// same instruction as "skip the state's hooks".
    /// </summary>
    [Theory]
    [InlineData("share-mark")]
    [InlineData("cancel")]
    public void Resolve_WhenANonUpdateDataTransitionTargetsSelf_ShouldReturnTheBaseProfileUnchanged(
        string transitionKey)
    {
        var context = CreateContext(TriggerType.Manual, isErrorBoundary: false);
        var transitionContext = CreateTransitionContext(
            TriggerType.Manual, WellKnownStateKeys.Self, transitionKey);

        var profile = _resolver.Resolve(context, transitionContext);

        profile.ShouldBeSameAs(PipelineExecutionProfile.ForManual());
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.OnEntry);
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.OnExit);
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.Schedule);
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.CancelScheduledJobs);
    }

    [Fact]
    public void Resolve_WhenErrorBoundaryAndUpdateDataSelfTarget_ShouldComposeOnTopOfErrorBoundary()
    {
        var context = CreateContext(TriggerType.Manual, isErrorBoundary: true);
        var transitionContext = CreateTransitionContext(
            TriggerType.Manual, WellKnownStateKeys.Self, WellKnownTransitionKeys.UpdateData);

        var profile = _resolver.Resolve(context, transitionContext);

        profile.Name.ShouldBe("ErrorBoundary+Self");
        profile.ExcludedStepOrders.ShouldContain(LifecycleOrder.ResourceLock);
        profile.ExcludedStepOrders.ShouldContain(LifecycleOrder.OnEntry);
    }

    /// <summary>
    /// The base profile must keep coming from the inbound workflow context's trigger, not from the
    /// transition definition's — the transition context prefers the definition's trigger type and
    /// the two can disagree.
    /// </summary>
    [Fact]
    public void Resolve_WithTransitionContext_ShouldTakeTheBaseProfileFromTheWorkflowContextTrigger()
    {
        var context = CreateContext(TriggerType.Manual, isErrorBoundary: false);
        var transitionContext = CreateTransitionContext(TriggerType.Event, "another-state");

        var profile = _resolver.Resolve(context, transitionContext);

        profile.ShouldBeSameAs(PipelineExecutionProfile.ForManual());
    }

    [Fact]
    public void Resolve_WhenTransitionContextNull_ShouldThrowArgumentNullException()
    {
        var context = CreateContext(TriggerType.Manual, isErrorBoundary: false);

        Should.Throw<ArgumentNullException>(() => _resolver.Resolve(context, null!));
    }

    /// <summary>
    /// Builds a transition context whose instance already sits in <see cref="CurrentStateKey"/>.
    /// <paramref name="transitionKey"/> defaults to a plain key; pass
    /// <see cref="WellKnownTransitionKeys.UpdateData"/> for the updateData cases — that is the only
    /// key the self variant is composed for.
    /// </summary>
    private static TransitionExecutionContext CreateTransitionContext(
        TriggerType triggerType,
        string target,
        string transitionKey = "transition")
    {
        var instance = Instance.Create(Guid.NewGuid(), "workflow", "1.0.0");
        instance.ChangeState(State.Create(CurrentStateKey, StateType.Intermediate, StateSubType.None, "Patch"));

        return new TransitionExecutionContext
        {
            Domain = "test-domain",
            InstanceId = instance.Id,
            WorkflowKey = "workflow",
            TransitionKey = transitionKey,
            Trigger = triggerType,
            Instance = instance,
            // Required: the updateData check reads Workflow.UpdateData for the configured-key case.
            Workflow = CreateWorkflow(),
            Transition = Transition.Create(transitionKey, CurrentStateKey, target, triggerType, "Patch"),
        };
    }

    /// <summary>A workflow with no updateData configured, so only the reserved alias matches.</summary>
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
                           { "key": "initial-contract", "stateType": "Intermediate", "transitions": [] }
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "initial-contract", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        return System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
    }

    private const string CurrentStateKey = "initial-contract";

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
