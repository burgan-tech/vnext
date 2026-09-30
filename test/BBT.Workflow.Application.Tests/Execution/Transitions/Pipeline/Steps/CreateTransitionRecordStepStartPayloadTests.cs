using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// Pins the start-payload hand-off: the start path maps the start payload and appends it as the
/// initial data version BEFORE the pipeline runs, so <see cref="CreateTransitionRecordStep"/> must
/// not map and append the same attributes again. That second pass took the row lock and re-read
/// the head only to dedup — and, with a non-deterministic mapping script, wrote a second version.
/// Everything that is not the start's own first hop must keep the normal map-and-append.
/// </summary>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public sealed class CreateTransitionRecordStepStartPayloadTests
{
    private readonly IInstanceTransitionRepository _transitionRepository =
        Substitute.For<IInstanceTransitionRepository>();
    private readonly IInstanceDataWriteService _dataWriteService = Substitute.For<IInstanceDataWriteService>();
    private readonly ITransitionDataMapper _dataMapper = Substitute.For<ITransitionDataMapper>();
    private readonly CreateTransitionRecordStep _step;

    public CreateTransitionRecordStepStartPayloadTests()
    {
        _dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(), Arg.Any<IRuntimeInfoProvider>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(new { amount = 1 }));

        _step = new CreateTransitionRecordStep(
            _transitionRepository,
            Substitute.For<IInstanceRepository>(),
            _dataWriteService,
            Substitute.For<IGuidGenerator>(),
            _dataMapper,
            Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<ILogger<CreateTransitionRecordStep>>());
    }

    [Fact]
    public async Task Start_whose_payload_was_already_appended_is_not_mapped_or_appended_again()
    {
        var context = CreateStartContext();
        context.StartPayloadPersisted = true;
        context.StartMappedPayload = new { amount = 1 };

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataMapper.DidNotReceiveWithAnyArgs().MapTransitionDataAsync(
            default, default, default!, default!, default!, default, default);
        await _dataWriteService.DidNotReceiveWithAnyArgs().AppendAsync(default!, default!, default, default, default);
        await _transitionRepository.Received(1)
            .InsertAsync(Arg.Any<InstanceTransition>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_without_the_flag_maps_and_appends_as_before()
    {
        // The async start's job re-entry rebuilds its context and never carries the flag.
        var context = CreateStartContext();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriteService.Received(1).AppendAsync(
            context.Instance, Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
            Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>());
    }

    [Fact]
    public async Task Flag_is_ignored_when_the_record_is_reused_by_a_retry()
    {
        var context = CreateStartContext();
        context.StartPayloadPersisted = true;
        var original = InstanceTransition.Create(
            Guid.NewGuid(), context.InstanceId, context.TransitionKey,
            "state1", TriggerType.Manual, new JsonData("{}"), new JsonData("{}"));
        context.RetryOfTransitionRecordId = original.Id;
        _transitionRepository.FindAsync(original.Id, true, Arg.Any<CancellationToken>()).Returns(original);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriteService.Received(1).AppendAsync(
            context.Instance, Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
            Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>());
    }

    [Fact]
    public async Task Flag_is_ignored_for_a_transition_other_than_the_start()
    {
        var context = CreateStartContext(transitionKey: "test-transition");
        context.StartPayloadPersisted = true;

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriteService.Received(1).AppendAsync(
            context.Instance, Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
            Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>());
    }

    private static TransitionExecutionContext CreateStartContext(string transitionKey = "start")
    {
        var workflow = CreateWorkflow();
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        var state = workflow.GetState("state1").Value!;
        instance.ChangeState(state);

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = workflow.ResolveTransition(transitionKey, state),
            Instance = instance,
            Data = new Dictionary<string, object?> { ["amount"] = 1 },
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
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
