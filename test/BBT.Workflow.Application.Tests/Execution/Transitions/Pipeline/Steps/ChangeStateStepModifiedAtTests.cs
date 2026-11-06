using System;
using System.Threading;
using System.Threading.Tasks;
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
/// How <see cref="ChangeStateStep"/> plans <c>Instances.ModifiedAt</c>. A state change is stamped here, riding
/// the UPDATE that writes the new state. A same-state hop (the start transition into the pre-positioned initial
/// state, a <c>$self</c> shared transition) is not — its save would write the row for the stamp alone — and is
/// marked pending instead, for <c>TransitionSettlement</c> to apply unless the Busy release stamps it. updateData
/// (its data row stamps it) and a deferred Finish save (its completion stamps it) are neither. Status ownership
/// plays no part: a job re-entry counts as the owner without having flipped anything, so the outcome is decided
/// where it is known, at the end of the hop.
/// </summary>
public class ChangeStateStepModifiedAtTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";
    private static readonly DateTime LongAgo = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly ChangeStateStep _step;

    public ChangeStateStepModifiedAtTests()
    {
        _step = new ChangeStateStep(_instanceRepository, Substitute.For<ILogger<ChangeStateStep>>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_state_change_is_stamped_here(bool ownsStatus)
    {
        var context = CreateContext("approve", "state2", ownsStatus);

        (await _step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Instance.GetCurrentState.ShouldBe("state2");
        context.Instance.ModifiedAt.ShouldNotBe(LongAgo);
        context.Directives.ModifiedAtStampPending.ShouldBeFalse();
    }

    /// <summary>
    /// A <c>$self</c> shared transition, owner or not: its save here would carry the stamp alone, so the stamp
    /// is left pending for the write that closes the hop.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_same_state_shared_transition_leaves_its_stamp_pending(bool ownsStatus)
    {
        var context = CreateContext("refresh", "$self", ownsStatus);

        (await _step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Instance.GetCurrentState.ShouldBe("state1");
        context.Instance.ModifiedAt.ShouldBe(LongAgo);
        context.Directives.ModifiedAtStampPending.ShouldBeTrue();
    }

    /// <summary>
    /// The start transition: creation pre-positions the instance into the initial state, so the start
    /// transition into that state changes nothing here — the most frequent same-state hop.
    /// </summary>
    [Fact]
    public async Task The_start_into_the_pre_positioned_initial_state_leaves_its_stamp_pending()
    {
        var context = CreateContext("start", "state1", ownsStatus: true);

        (await _step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Instance.ModifiedAt.ShouldBe(LongAgo);
        context.Directives.ModifiedAtStampPending.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateData_is_left_to_its_data_row()
    {
        var context = CreateContext("sync-data", "$self", ownsStatus: false);

        (await _step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Instance.ModifiedAt.ShouldBe(LongAgo);
        context.Directives.ModifiedAtStampPending.ShouldBeFalse();
    }

    [Fact]
    public async Task A_deferred_finish_save_is_left_to_its_completion()
    {
        var context = CreateContext("finish", "done", ownsStatus: true);

        (await _step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Instance.GetCurrentState.ShouldBe("done");
        context.Instance.ModifiedAt.ShouldBe(LongAgo);
        context.Directives.ModifiedAtStampPending.ShouldBeFalse();
        await _instanceRepository.Received(1).UpdateAsync(context.Instance, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_timeout_to_another_state_is_stamped_here()
    {
        var context = CreateContext("approve", "state2", ownsStatus: true);
        context.Directives.MarkAsTimeoutTransition();
        context.Target = context.Workflow.GetState("state2").Value!;

        (await _step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Instance.ModifiedAt.ShouldNotBe(LongAgo);
        context.Directives.ModifiedAtStampPending.ShouldBeFalse();
    }

    [Fact]
    public async Task A_timeout_into_the_same_state_leaves_its_stamp_pending()
    {
        var context = CreateContext("approve", "state1", ownsStatus: true);
        context.Directives.MarkAsTimeoutTransition();
        context.Target = context.Workflow.GetState("state1").Value!;

        (await _step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Instance.ModifiedAt.ShouldBe(LongAgo);
        context.Directives.ModifiedAtStampPending.ShouldBeTrue();
    }

    private static TransitionExecutionContext CreateContext(string transitionKey, string target, bool ownsStatus)
    {
        var workflow = CreateWorkflow();
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, "1.0.0");
        instance.ChangeState(workflow.GetState("state1").Value!);
        instance.ModifiedAt = LongAgo;

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Transition = Transition.Create(transitionKey, "state1", target, TriggerType.Manual, "Patch"),
            Instance = instance,
            OwnsStatus = ownsStatus,
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
                           { "key": "state1", "stateType": "Intermediate", "transitions": [] },
                           { "key": "state2", "stateType": "Intermediate", "transitions": [] },
                           { "key": "done", "stateType": "Finish", "transitions": [] }
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null},
                       "updateData": {"key": "sync-data", "target": "$self", "triggerType": "Manual", "versionStrategy": "Patch", "labels": []}
                   }
                   """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
