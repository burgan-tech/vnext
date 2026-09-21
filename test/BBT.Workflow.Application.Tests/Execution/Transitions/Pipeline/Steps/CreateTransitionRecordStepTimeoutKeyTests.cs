using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// How <see cref="CreateTransitionRecordStep"/> (order 20) resolves the virtual <c>$timeout</c> key
/// into the audit record's transition key.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this pins was found by running it, not by reading it.</b> The fire path's own fix
/// lives in <c>ApplyTimeoutStateStep</c> at order <b>38</b> — but this step runs at <b>20</b>, and
/// it resolved the key through <c>Workflow.ResolveWellKnownKey</c>, which throws
/// <c>TimeoutNotConfiguredForWorkflowException</c> when the workflow declares no timeout of its own.
/// For a SubFlow child running on a parent-supplied <c>subFlow.overrides.timeout</c> that is exactly
/// the case, so the pipeline died here, three steps before the fix could apply.
/// </para>
/// <para>
/// The measured consequence was worse than "the timeout does not fire": <c>SetBusy</c> (19) had
/// already committed, so the child was left <b>Busy in its waiting state forever</b> with its job
/// row marked processed — no deadline, and no way back either. Two `timeout-lab` children reproduced
/// it on the bench before this changed.
/// </para>
/// <para>
/// <c>Workflow.ResolveWellKnownKey</c> is deliberately left alone: it is a definition-level method
/// with no instance in scope, and it is right about what it can see. The instance-aware answer
/// belongs here, where <c>context.Instance</c> is in hand — the same rule the other three call sites
/// follow.
/// </para>
/// </remarks>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public class CreateTransitionRecordStepTimeoutKeyTests
{
    private readonly IInstanceTransitionRepository _transitionRepository =
        Substitute.For<IInstanceTransitionRepository>();
    private readonly CreateTransitionRecordStep _step;

    public CreateTransitionRecordStepTimeoutKeyTests()
    {
        var dataMapper = Substitute.For<ITransitionDataMapper>();
        dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(), Arg.Any<IRuntimeInfoProvider>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(null));

        _step = new CreateTransitionRecordStep(
            _transitionRepository,
            Substitute.For<IInstanceRepository>(),
            Substitute.For<IInstanceDataWriteService>(),
            Substitute.For<IGuidGenerator>(),
            dataMapper,
            Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<ILogger<CreateTransitionRecordStep>>());
    }

    [Fact]
    public async Task TimeoutKey_ResolvesThroughTheWorkflowsOwnTimeout_WhenThereIsNoOverride()
    {
        var context = CreateTimeoutContext(overrideStamp: null, ownTimeoutKey: "root-abandoned");

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _transitionRepository.Received(1).InsertAsync(
            Arg.Is<InstanceTransition>(t => t.TransitionId == "root-abandoned"),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The regression. Before the fix this threw <c>TimeoutNotConfiguredForWorkflowException</c>
    /// out of the step and stranded the instance Busy.
    /// </summary>
    [Fact]
    public async Task TimeoutKey_ResolvesThroughTheSubFlowOverride_WhenTheChildDeclaresNoTimeout()
    {
        var context = CreateTimeoutContext(
            overrideStamp: Stamp("child-abandoned", "child-timedout"),
            ownTimeoutKey: null);

        context.Workflow.Timeout.ShouldBeNull();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _transitionRepository.Received(1).InsertAsync(
            Arg.Is<InstanceTransition>(t => t.TransitionId == "child-abandoned"),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TimeoutKey_PrefersTheOverride_OverTheChildsOwnTimeout()
    {
        var context = CreateTimeoutContext(
            overrideStamp: Stamp("child-abandoned", "child-timedout"),
            ownTimeoutKey: "own-abandoned");

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _transitionRepository.Received(1).InsertAsync(
            Arg.Is<InstanceTransition>(t => t.TransitionId == "child-abandoned"),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An ordinary transition key is untouched — the instance-aware branch is scoped to
    /// <c>$timeout</c> and must not reinterpret anything else.
    /// </summary>
    [Fact]
    public async Task OrdinaryTransitionKey_IsNotReinterpreted()
    {
        var context = CreateTimeoutContext(
            overrideStamp: null, ownTimeoutKey: "root-abandoned", transitionKey: "test-transition");

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _transitionRepository.Received(1).InsertAsync(
            Arg.Is<InstanceTransition>(t => t.TransitionId == "test-transition"),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static string Stamp(string key, string target) =>
        JsonSerializer.Serialize(
            WorkflowTimeout.Create(key, target, "Minor", "never", "PT20S"),
            JsonSerializerConstants.JsonOptions);

    private static TransitionExecutionContext CreateTimeoutContext(
        string? overrideStamp, string? ownTimeoutKey, string? transitionKey = null)
    {
        var workflow = CreateWorkflow();
        if (ownTimeoutKey is not null)
        {
            workflow.SetTimeout(WorkflowTimeout.Create(
                ownTimeoutKey, "state1", VersionStrategy.IncreasePatch.Code, "never", "PT1H"));
        }

        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        instance.ChangeState(workflow.GetState("state1").Value!);

        if (overrideStamp is not null)
        {
            instance.SetMetaData(new ExtraPropertyDictionary
            {
                [DomainConsts.MetaDataKeys.TimeoutOverride] = overrideStamp
            });
        }

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = transitionKey ?? WellKnownTransitionKeys.Timeout,
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
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
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
