using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Headers;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// <see cref="CreateTransitionRecordStep"/> and <c>Workflow:HeaderExposure:Deny</c>: a listed header is never written to
/// the transition record, while the hop's own <see cref="TransitionExecutionContext.Headers"/> keeps it — the runtime's
/// role, identity and credential reads use it for the rest of the hop.
/// </summary>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public class CreateTransitionRecordStepHeaderExposureTests
{
    private readonly IInstanceTransitionRepository _transitionRepository = Substitute.For<IInstanceTransitionRepository>();

    private CreateTransitionRecordStep Step(params string[] deny)
    {
        var dataMapper = Substitute.For<ITransitionDataMapper>();
        dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<Action<Exception>?>(), Arg.Any<BBT.Workflow.Shared.ExecutionActor?>(), Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(null));

        return new CreateTransitionRecordStep(
            _transitionRepository,
            Substitute.For<IInstanceRepository>(),
            Substitute.For<IInstanceDataWriteService>(),
            Substitute.For<IGuidGenerator>(),
            dataMapper,
            BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
            Substitute.For<ILogger<CreateTransitionRecordStep>>(),
            headerExposureDenyList: new HeaderExposureDenyList(Options.Create(new HeaderExposureOptions { Deny = [.. deny] })));
    }

    private static Dictionary<string, string?> RequestHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Authorization"] = "Bearer t",
        ["Cookie"] = "c=1",
        ["sub"] = "u-1",
        ["x-request-id"] = "abc",
    };

    [Fact]
    public async Task ADeniedHeader_IsNotWrittenToTheRecord_AndStaysOnTheHop()
    {
        var context = Context(RequestHeaders());
        InstanceTransition? recorded = null;
        await _transitionRepository.InsertAsync(
            Arg.Do<InstanceTransition>(t => recorded = t), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        var result = await Step("authorization", "COOKIE").ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var names = recorded!.Header.JsonElement.EnumerateObject().Select(p => p.Name).ToArray();
        names.ShouldBe(["sub", "x-request-id"]);
        context.Headers.ContainsKey("Authorization").ShouldBeTrue();
        context.Headers.ContainsKey("Cookie").ShouldBeTrue();
    }

    [Fact]
    public async Task WithAnEmptyList_EveryHeaderIsRecorded_AsToday()
    {
        var context = Context(RequestHeaders());
        InstanceTransition? recorded = null;
        await _transitionRepository.InsertAsync(
            Arg.Do<InstanceTransition>(t => recorded = t), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        var result = await Step().ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        recorded!.Header.JsonElement.GetRawText()
            .ShouldBe(JsonSerializer.Serialize(context.Headers));
    }

    private static TransitionExecutionContext Context(Dictionary<string, string?> headers)
    {
        var workflow = Workflow();
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        instance.ChangeState(workflow.GetState("state1").Value!);

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Instance = instance,
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16],
            Headers = headers,
        };
    }

    private static Definitions.Workflow Workflow()
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
