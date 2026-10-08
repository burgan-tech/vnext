using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using BBT.Workflow.Files;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Logging;
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
/// Pins the x-storage swap on start (spec §3): it runs after start-transition validation and before
/// the instance row is inserted, so a store failure creates no instance; a swapped payload replaces the
/// start attributes and drops the raw body.
/// </summary>
public class InstanceCommandAppServiceStartFilesTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";
    private const string Version = "1.0.0";

    private static readonly JsonElement Incoming = JsonDocument.Parse(
        """{ "passport": { "name": "p.pdf", "content": "AAEC" } }""").RootElement.Clone();
    private static readonly JsonElement Swapped = JsonDocument.Parse(
        """{ "passport": { "component": "vnext-blob-local", "file": "6f1c", "name": "p.pdf" } }""").RootElement.Clone();

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly ITransitionValidationService _validation = Substitute.For<ITransitionValidationService>();
    private readonly IFileOffloadService _offload = Substitute.For<IFileOffloadService>();
    private readonly IRequestRawBodyProvider _rawBody = Substitute.For<IRequestRawBodyProvider>();
    private readonly IWorkflowExecutionService _execution = Substitute.For<IWorkflowExecutionService>();
    private readonly List<string> _order = new();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceCommandAppServiceStartFilesTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        mockUoWManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(Substitute.For<IUnitOfWork>());
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        // PrepareInstanceAsync opens its unit of work through ApplicationService.UnitOfWorkManager.
        services.AddSingleton<ILazyServiceProvider>(sp => new LazyServiceProvider(sp));
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", Version));
        workflow.SetType("F");
        _componentCacheStore.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

        _validation.ValidateStartTransitionAsync(default!, default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(_ => { _order.Add("validate"); return Result.Ok(); });
        _offload.OffloadAsync(default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                _order.Add("offload");
                return Result<FileOffloadResult>.Ok(new FileOffloadResult(Swapped, Changed: true));
            });
        _offload.GetFieldsAsync(default!, default).ReturnsForAnyArgs(
            (IReadOnlyList<FileStorageField>)[new FileStorageField(["passport"], "vnext-blob-local")]);
        _instanceRepository.InsertAsync(default!, default, default)
            .ReturnsForAnyArgs(ci => { _order.Add("insert"); return ci.Arg<Instance>(); });

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
            transitionDataMapper: Substitute.For<ITransitionDataMapper>(),
            transitionValidationService: _validation,
            transitionAdmissionService: Substitute.For<ITransitionAdmissionService>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new BBT.Workflow.Instances.InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            fileOffloadService: _offload,
            rawBodyProvider: _rawBody,
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task StartAsync_FileStoreFails_CreatesNoInstance()
    {
        _offload.OffloadAsync(default!, default)
            .ReturnsForAnyArgs(Result<FileOffloadResult>.Fail(WorkflowErrors.FileStoreUnavailable("vnext-blob-local")));

        var result = await _service.StartAsync(CreateInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
        await _instanceRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
        await _execution.DidNotReceiveWithAnyArgs().ExecuteTransitionAsync(default!, default);
        _rawBody.DidNotReceiveWithAnyArgs().ReplaceRawBodyAttributes(default);
    }

    [Fact]
    public async Task StartAsync_WithContent_SwapsAfterValidationAndBeforeTheInsert()
    {
        var input = CreateInput();

        await _service.StartAsync(input, CancellationToken.None);

        _order.Take(3).ShouldBe(new[] { "validate", "offload", "insert" });
        input.Instance.Attributes!.Value.GetRawText().ShouldBe(Swapped.GetRawText());
        _rawBody.Received(1).ReplaceRawBodyAttributes(
            Arg.Is<JsonElement?>(e => e.HasValue && e.Value.GetRawText() == Swapped.GetRawText()));
        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.Mode == FileOffloadMode.External && !r.LatestData.HasValue
                                            && r.Payload!.Value.GetRawText() == Incoming.GetRawText()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_TrustedPayload_PassesTrustedMode()
    {
        var input = CreateInput();
        input.TrustedPayload = true;

        await _service.StartAsync(input, CancellationToken.None);

        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.Mode == FileOffloadMode.Trusted), Arg.Any<CancellationToken>());
        // A trusted start (subflow input, trigger task) must not overwrite the outer request's raw body.
        _rawBody.DidNotReceiveWithAnyArgs().ReplaceRawBodyAttributes(default);
    }

    [Fact]
    public async Task StartAsync_Unchanged_KeepsAttributesAndRawBody()
    {
        _offload.OffloadAsync(default!, default)
            .ReturnsForAnyArgs(Result<FileOffloadResult>.Ok(new FileOffloadResult(Incoming, Changed: false)));
        var input = CreateInput();

        await _service.StartAsync(input, CancellationToken.None);

        input.Instance.Attributes!.Value.GetRawText().ShouldBe(Incoming.GetRawText());
        _rawBody.DidNotReceiveWithAnyArgs().ReplaceRawBodyAttributes(default);
        await _instanceRepository.ReceivedWithAnyArgs(1).InsertAsync(default!, default, default);
    }

    /// <summary>No x-storage field in the flow ⇒ no offload call and no Files.Offload span on start.</summary>
    [Fact]
    public async Task StartAsync_FlowWithoutXStorage_SkipsTheOffload()
    {
        _offload.GetFieldsAsync(default!, default).ReturnsForAnyArgs((IReadOnlyList<FileStorageField>)[]);
        var input = CreateInput();

        await _service.StartAsync(input, CancellationToken.None);

        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        input.Instance.Attributes!.Value.GetRawText().ShouldBe(Incoming.GetRawText());
        await _instanceRepository.ReceivedWithAnyArgs(1).InsertAsync(default!, default, default);
    }

    private static StartInstanceInput CreateInput()
        => new(Domain, Flow, Version)
        {
            Instance = new CreateInstanceInput { Key = "k-" + Guid.NewGuid(), Attributes = Incoming },
            StrictIdempotency = true
        };
}
