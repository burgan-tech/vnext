using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
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
/// The start hop of an unmapped start transition carries exactly the payload the instance's creation has just
/// stored as its initial version. Appending it again could only dedup to no row, after a transaction, the
/// per-instance row lock, a head read and a commit — so the hop skips it, and still records itself.
/// </summary>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public class CreateTransitionRecordStepInitialDataTests
{
    private readonly IInstanceDataWriteService _dataWriteService = Substitute.For<IInstanceDataWriteService>();
    private readonly IInstanceTransitionRepository _transitionRepository = Substitute.For<IInstanceTransitionRepository>();
    private readonly CreateTransitionRecordStep _step;

    public CreateTransitionRecordStepInitialDataTests()
    {
        var dataMapper = Substitute.For<ITransitionDataMapper>();
        // The unmapped mapper hands the payload through, as TransitionDataMapper does.
        dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<Action<Exception>?>(),
                Arg.Any<BBT.Workflow.Shared.ExecutionActor?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result<object?>.Ok(ci.ArgAt<object?>(0)));

        _step = new CreateTransitionRecordStep(
            _transitionRepository,
            Substitute.For<IInstanceRepository>(),
            _dataWriteService,
            Substitute.For<IGuidGenerator>(),
            dataMapper,
            BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
            Substitute.For<ILogger<CreateTransitionRecordStep>>());
    }

    [Fact]
    public async Task Start_hop_whose_payload_the_creation_stored_appends_nothing_but_still_records_the_hop()
    {
        var context = CreateStartContext(initialDataPersisted: true);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriteService.DidNotReceiveWithAnyArgs().AppendAsync(
            default!, default!, default, default, default, default);
        await _transitionRepository.Received(1).InsertAsync(
            Arg.Any<InstanceTransition>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_hop_without_the_creation_claim_appends_its_payload_as_before()
    {
        // An async start's job re-entry, a mapped start transition and a history: none creation never set
        // the claim: the hop keeps its append.
        var context = CreateStartContext(initialDataPersisted: false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriteService.Received(1).AppendAsync(
            Arg.Any<Instance>(), Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
            Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>(), Arg.Any<bool>());
    }

    private static TransitionExecutionContext CreateStartContext(bool initialDataPersisted)
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
            TransitionKey = "start",
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = workflow.StartTransition,
            Instance = instance,
            Data = JsonDocument.Parse("""{"userName":"1234"}""").RootElement.Clone(),
            InitialDataPersisted = initialDataPersisted,
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
                           {"key": "state1", "stateType": "Intermediate", "transitions": []}
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
