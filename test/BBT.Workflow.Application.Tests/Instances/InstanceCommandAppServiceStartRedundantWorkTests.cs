using BBT.Aether.BackgroundJob;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Gateway;
using BBT.Workflow.Headers;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Two pieces of work a sync start used to repeat for nothing (morph-idm subflow cost report, test, 2026-11-27):
/// <list type="bullet">
///   <item>The start hop re-appended the payload the creation had just stored. For an unmapped start transition
///   that append is a guaranteed no-op, so the start tells the pipeline to skip it
///   (<see cref="WorkflowExecutionContext.InitialDataPersisted"/>); a mapped one, a history: none flow and a start
///   with no payload keep it.</item>
///   <item>The response re-read the instance although the run handed back its committed instance (the settlement's
///   reload, data included, when the start entered a SubFlow). It is reused like on the transition path; only a
///   Busy one is read again.</item>
/// </list>
/// </summary>
public class InstanceCommandAppServiceStartRedundantWorkTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IWorkflowExecutionService _execution = Substitute.For<IWorkflowExecutionService>();
    private readonly ITransitionDataMapper _dataMapper = Substitute.For<ITransitionDataMapper>();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;
    private WorkflowExecutionContext? _captured;

    public InstanceCommandAppServiceStartRedundantWorkTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        mockUoWManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(Substitute.For<IUnitOfWork>());
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton<ILazyServiceProvider>(sp => new LazyServiceProvider(sp));
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        UseWorkflow(CreateWorkflow(historyNone: false, mapped: false));

        var validation = Substitute.For<ITransitionValidationService>();
        validation.ValidateStartTransitionAsync(default!, default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(Result.Ok());
        _instanceRepository.InsertAsync(default!, default, default)
            .ReturnsForAnyArgs(ci => ci.Arg<Instance>());
        _dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(), Arg.Any<Instance>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<Action<Exception>?>(),
                Arg.Any<BBT.Workflow.Shared.ExecutionActor?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result<object?>.Ok(ci.ArgAt<object?>(0)));
        ReturnFromExecution(new TransitionOutput { Id = Guid.NewGuid(), Status = InstanceStatus.Active });

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: _execution,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceDataWriteService: Substitute.For<IInstanceDataWriteService>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: _dataMapper,
            transitionValidationService: validation,
            transitionAdmissionService: Substitute.For<ITransitionAdmissionService>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            fileOffloadService: BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
            rawBodyProvider: Substitute.For<IRequestRawBodyProvider>(),
            subflowProxyService: Substitute.For<BBT.Workflow.SubFlow.ISubflowProxyService>(),
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task Start_with_a_payload_and_an_unmapped_start_transition_tells_the_pipeline_its_data_is_stored()
    {
        var result = await _service.StartAsync(CreateInput(withPayload: true), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _captured.ShouldNotBeNull();
        _captured.InitialDataPersisted.ShouldBeTrue();
    }

    [Fact]
    public async Task Start_without_a_payload_leaves_the_append_to_the_pipeline()
    {
        // The creation maps and stores nothing when there is no payload, so the pipeline's own run is the only one.
        var result = await _service.StartAsync(CreateInput(withPayload: false), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _captured!.InitialDataPersisted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Start_of_a_mapped_or_history_none_flow_keeps_the_pipeline_append(bool historyNone, bool mapped)
    {
        // Mapped: the script runs again in the pipeline and may give another result. history: none: the creation
        // wrote no row at all; the pipeline buffers the data for its single write.
        UseWorkflow(CreateWorkflow(historyNone, mapped));

        var result = await _service.StartAsync(CreateInput(withPayload: true), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _captured!.InitialDataPersisted.ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_start_projects_the_committed_instance_the_run_returned_without_reading_it_again()
    {
        var settled = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "k");
        settled.Complete(Domain);
        ReturnFromExecution(new TransitionOutput { Id = settled.Id, Status = InstanceStatus.Busy, PipelineInstance = settled });

        var result = await _service.StartAsync(CreateInput(withPayload: true), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        // The run reported Busy (the parent's handoff); the settled instance it carried says Completed.
        result.Value!.Status.ShouldBe(InstanceStatus.Completed);
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindByIdentifierAsReadOnlyAsync(default!, default);
    }

    [Fact]
    public async Task Sync_start_still_reads_the_instance_when_the_run_returned_it_Busy()
    {
        // A Busy instance may be stale (a sync child resumes the parent in its own scope), so it is read again.
        var busy = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "k");
        busy.Busy();
        ReturnFromExecution(new TransitionOutput { Id = busy.Id, Status = InstanceStatus.Busy, PipelineInstance = busy });
        _instanceRepository.FindByIdentifierAsReadOnlyAsync(busy.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(busy);

        var result = await _service.StartAsync(CreateInput(withPayload: true), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_the_definition_forced_to_sync_reads_the_instance_again()
    {
        // executionType S with a caller that asked sync=false: the run is sync and the response is projected, but
        // the settlement loaded the data row by the caller's mode (async ⇒ none). Reusing that instance would answer
        // with no attributes, so this start keeps the read.
        UseWorkflow(CreateWorkflow(historyNone: false, mapped: false, executionType: "S"));
        var settled = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "k");
        settled.Complete(Domain);
        ReturnFromExecution(new TransitionOutput { Id = settled.Id, Status = InstanceStatus.Busy, PipelineInstance = settled });
        _instanceRepository.FindByIdentifierAsReadOnlyAsync(settled.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(settled);
        var input = CreateInput(withPayload: true);
        input.Sync = false;

        var result = await _service.StartAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.ExecutedAsync.ShouldBe(false);
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private void ReturnFromExecution(TransitionOutput output) =>
        _execution.ExecuteTransitionAsync(Arg.Do<WorkflowExecutionContext>(c => _captured = c), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(output));

    private void UseWorkflow(Definitions.Workflow workflow) =>
        _componentCacheStore.GetFlowAsync(Domain, Flow, "1.0", Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

    private static StartInstanceInput CreateInput(bool withPayload)
        => new(Domain, Flow, "1.0", sync: true)
        {
            Instance = new CreateInstanceInput
            {
                Key = "k-" + Guid.NewGuid(),
                Attributes = withPayload ? JsonDocument.Parse("""{"userName":"1234"}""").RootElement.Clone() : null
            },
            StrictIdempotency = true
        };

    private static Definitions.Workflow CreateWorkflow(bool historyNone, bool mapped, string? executionType = null)
    {
        var history = (historyNone ? "\"history\": \"none\"," : string.Empty)
                      + (executionType is null ? string.Empty : $"\"executionType\": \"{executionType}\",");
        var json = $$"""
                   {
                       "type": "F",
                       {{history}}
                       "labels": [],
                       "states": [
                           {"key": "s1", "stateType": "Finish", "transitions": []}
                       ],
                       "startTransition": {"key": "start", "target": "s1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": []}
                   }
                   """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", "1.0.0"));
        if (mapped)
            workflow.StartTransition.SetMapping(new ScriptCode("inline", "return input;"));
        return workflow;
    }
}
