using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions.Factory;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Factory;

/// <summary>
/// The start hop of a request that has just created its instance takes that instance instead of
/// reading the row back (<see cref="WorkflowExecutionContext.CreatedInstance"/>). These pin the
/// handover: attached through the repository, used once, and every case that cannot attach still
/// loads the row exactly as before.
/// </summary>
public sealed class TransitionContextFactoryCreatedInstanceTests
{
    private readonly IInstanceRepository _repository = Substitute.For<IInstanceRepository>();
    private readonly Definitions.Workflow _workflow = CreateWorkflow();
    private readonly Instance _created;
    private readonly TransitionContextFactory _factory;

    public TransitionContextFactoryCreatedInstanceTests()
    {
        _created = Instance.Create(Guid.NewGuid(), _workflow.Key, _workflow.Version);
        _created.ChangeState(_workflow.GetState("state1").Value!);
        _repository.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(_created));
        _factory = new TransitionContextFactory(
            _repository, Substitute.For<IComponentCacheStore>(), Substitute.For<IRuntimeInfoProvider>());
    }

    [Fact]
    public async Task The_created_instance_is_attached_and_the_row_is_not_read()
    {
        _repository.TryAttachCreatedAsync(_created, Arg.Any<CancellationToken>()).Returns(true);
        var input = Input(_created.Id, created: _created);

        var context = (await _factory.CreateAsync(input, CancellationToken.None)).Value!;

        context.Instance.ShouldBeSameAs(_created);
        await _repository.DidNotReceiveWithAnyArgs().GetActiveAsync(default!, default);
        // Same post-load steps as a loaded instance.
        await _repository.Received(1).LoadActiveIncidentsAsync(_created, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_carrier_is_used_once()
    {
        _repository.TryAttachCreatedAsync(_created, Arg.Any<CancellationToken>()).Returns(true);
        var input = Input(_created.Id, created: _created);

        await _factory.CreateAsync(input, CancellationToken.None);
        input.CreatedInstance.ShouldBeNull();
        await _factory.CreateAsync(input, CancellationToken.None);

        await _repository.Received(1).TryAttachCreatedAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).GetActiveAsync(_created.Id.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_instance_the_unit_of_work_cannot_attach_is_loaded_as_before()
    {
        _repository.TryAttachCreatedAsync(_created, Arg.Any<CancellationToken>()).Returns(false);

        var context = (await _factory.CreateAsync(Input(_created.Id, created: _created), CancellationToken.None)).Value!;

        context.Instance.ShouldBeSameAs(_created); // the loaded one, from the stub
        await _repository.Received(1).GetActiveAsync(_created.Id.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_carrier_for_another_instance_is_ignored()
    {
        var other = Guid.NewGuid();

        await _factory.CreateAsync(Input(other, created: _created), CancellationToken.None);

        await _repository.DidNotReceiveWithAnyArgs().TryAttachCreatedAsync(default!, default);
        await _repository.Received(1).GetActiveAsync(other.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_carrier_the_row_is_loaded()
    {
        await _factory.CreateAsync(Input(_created.Id, created: null), CancellationToken.None);

        await _repository.DidNotReceiveWithAnyArgs().TryAttachCreatedAsync(default!, default);
        await _repository.Received(1).GetActiveAsync(_created.Id.ToString(), Arg.Any<CancellationToken>());
    }

    private WorkflowExecutionContext Input(Guid instanceId, Instance? created) => new()
    {
        Domain = _workflow.Domain, WorkflowKey = _workflow.Key, WorkflowVersion = _workflow.Version,
        InstanceId = instanceId.ToString(), TransitionKey = "start", Mode = ExecMode.Sync,
        ResolvedWorkflow = _workflow,
        CreatedInstance = created
    };

    private static Definitions.Workflow CreateWorkflow()
    {
        const string json = """
        {
            "type": "F", "timeout": null, "labels": [], "functions": [], "features": [],
            "states": [ {"key": "state1", "type": "P", "transitions": []} ],
            "sharedTransitions": [], "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("created-flow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
