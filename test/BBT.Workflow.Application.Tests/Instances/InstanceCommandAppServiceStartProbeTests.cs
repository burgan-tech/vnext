using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Extentions;
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
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the start idempotency probe's branch selection in
/// <see cref="InstanceCommandAppService.StartAsync"/>: a typed <c>Instance.Id</c> goes through the
/// PK-only <see cref="IInstanceRepository.FindLeanByIdAsync"/> — never the generic identifier
/// resolver, whose key fallback fires a second guaranteed-miss full-row query on every fresh
/// subprocess start — while a key keeps using <see cref="IInstanceRepository.FindActiveByKeyLeanAsync"/>.
/// </summary>
public class InstanceCommandAppServiceStartProbeTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";
    private const string Version = "1.0.0";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceCommandAppServiceStartProbeTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        _componentCacheStore.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(CreateWorkflow()));

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: Substitute.For<IWorkflowExecutionService>(),
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceDataWriteService: Substitute.For<IInstanceDataWriteService>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: Substitute.For<ITransitionDataMapper>(),
            transitionValidationService: Substitute.For<ITransitionValidationService>(),
            transitionAdmissionService: Substitute.For<ITransitionAdmissionService>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            currentUser: Substitute.For<ICurrentUser>(),
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task StartAsync_IdOnlyInput_ProbesByPkWithoutIdentifierResolver()
    {
        // The subprocess-start shape: fresh typed Guid id, no key, strict idempotency.
        var instanceId = Guid.NewGuid();
        var existing = Instance.Create(instanceId, Flow, Version, "occupied-key");
        _instanceRepository.FindLeanByIdAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _service.StartAsync(CreateInput(id: instanceId, strict: true), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ActiveInstanceAlreadyExists);

        // The whole point: the generic identifier resolver (id probe + key-string fallback,
        // two full-row queries on a miss) is never consulted for a typed id.
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .GetResultAsync(default!, default, default);
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .FindActiveByKeyLeanAsync(default!, default);
    }

    [Fact]
    public async Task StartAsync_IdOnlyInput_NonStrict_ReturnsExistingIdempotently()
    {
        var instanceId = Guid.NewGuid();
        var existing = Instance.Create(instanceId, Flow, Version, "occupied-key");
        _instanceRepository.FindLeanByIdAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _service.StartAsync(CreateInput(id: instanceId, strict: false), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Id.ShouldBe(instanceId);
        result.Value.Key.ShouldBe("occupied-key");
    }

    [Fact]
    public async Task StartAsync_KeyInput_KeepsActiveKeyProbe()
    {
        // A guid-shaped key must still resolve through the deterministic non-terminal key probe,
        // not through the new PK lookup.
        var key = Guid.NewGuid().ToString();
        var existing = Instance.Create(Guid.NewGuid(), Flow, Version, key);
        _instanceRepository.FindActiveByKeyLeanAsync(key, Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _service.StartAsync(CreateInput(key: key, strict: true), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ActiveInstanceAlreadyExists);
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .FindLeanByIdAsync(default, default);
    }

    private static StartInstanceInput CreateInput(Guid? id = null, string? key = null, bool strict = true)
        => new(Domain, Flow, Version)
        {
            Instance = new CreateInstanceInput { Id = id, Key = key },
            StrictIdempotency = strict
        };

    private static Definitions.Workflow CreateWorkflow()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", Version));
        return workflow;
    }
}
