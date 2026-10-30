using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.SubFlow;
using BBT.Workflow.SubFlow.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// Covers the classification split inside <see cref="SubflowOutputMappingService.ApplyAsync"/> itself —
/// the caller-contract tests in <c>SubflowCompletionServiceTests</c> stub the whole service and never
/// exercise the real catch blocks this fix added.
/// </summary>
public sealed class SubflowOutputMappingServiceTests
{
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<IInstanceDataWriteService> _instanceDataWriteService = new();
    private readonly Mock<IScriptEngine> _scriptEngine = new();
    private readonly Mock<IScriptContextFactory> _scriptContextFactory = new();
    private readonly Mock<IRuntimeInfoProvider> _runtimeInfoProvider = new();
    private readonly Mock<IGuidGenerator> _guidGenerator = new();
    private readonly Mock<ILogger<SubflowOutputMappingService>> _logger = new();

    public SubflowOutputMappingServiceTests()
    {
        _scriptEngine
            .Setup(x => x.CompileToInstanceAsync<object>(
                It.IsAny<ScriptCode>(),
                It.IsAny<ScriptSettings>(),
                It.IsAny<IEnumerable<MetadataReference>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<ISubFlowMapping>());
    }

    [Fact]
    public async Task ApplyAsync_WhenScriptContextBuildThrowsUnclassifiedAssemblyLoadFailure_ShouldReturnFailedResult()
    {
        var parentInstance = CreateParentInstance();
        var parentWorkflow = CreateParentWorkflowWithMapping();
        _scriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Throws(new FileLoadException("Assembly with same name is already loaded"));

        var result = await CreateService().ApplyAsync(
            parentInstance, parentWorkflow, "waiting-child", CreateChildData(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.SubflowOutputMappingFailed);
    }

    [Fact]
    public async Task ApplyAsync_WhenScriptContextBuildThrowsBadImageFormat_ShouldReturnFailedResult()
    {
        var parentInstance = CreateParentInstance();
        var parentWorkflow = CreateParentWorkflowWithMapping();
        _scriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Throws(new BadImageFormatException("invalid assembly image"));

        var result = await CreateService().ApplyAsync(
            parentInstance, parentWorkflow, "waiting-child", CreateChildData(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.SubflowOutputMappingFailed);
    }

    [Fact]
    public async Task ApplyAsync_WhenScriptContextBuildFailsPermanently_ShouldReturnFailedResultWithoutThrowing()
    {
        var parentInstance = CreateParentInstance();
        var parentWorkflow = CreateParentWorkflowWithMapping();
        _scriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Throws(new NotSupportedException("mapping script is invalid"));

        var result = await CreateService().ApplyAsync(
            parentInstance, parentWorkflow, "waiting-child", CreateChildData(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.SubflowOutputMappingFailed);
    }

    /// <summary>
    /// The two tests above throw from the very first statement in the `try` — the weakest point in
    /// the method, since a narrowed `try` or an inner catch would still leave them green. This test
    /// reproduces the actual production failure site: the script-context build succeeds and
    /// <c>scriptEngine.CompileToInstanceAsync</c> is what throws, several statements deeper, exactly
    /// where the real `FileLoadException` surfaced in the incident.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WhenScriptCompilationThrowsUnclassifiedAssemblyLoadFailure_ShouldReturnFailedResult()
    {
        var parentInstance = CreateParentInstance();
        var parentWorkflow = CreateParentWorkflowWithMapping();
        SetupScriptContextFactory();
        _scriptEngine
            .Setup(x => x.CompileToInstanceAsync<object>(
                It.IsAny<ScriptCode>(),
                It.IsAny<ScriptSettings>(),
                It.IsAny<IEnumerable<MetadataReference>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileLoadException("Assembly with same name is already loaded"));

        var result = await CreateService().ApplyAsync(
            parentInstance, parentWorkflow, "waiting-child", CreateChildData(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.SubflowOutputMappingFailed);
    }

    /// <summary>
    /// A cancellation-shaped exception (e.g. <see cref="TaskCanceledException"/> from a
    /// <c>DaprClient</c> call inside the mapping timing out) arriving while OUR token is NOT
    /// cancelled is a downstream fault, not "our own cancellation". It must fault the parent visibly;
    /// redelivery would only spend the bounded Inbox retry budget before dead-lettering the event and
    /// leaving the parent correlation open.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WhenCancellationExceptionArrivesWithoutOurTokenCancelled_ShouldReturnFailedResultWithoutThrowing()
    {
        var parentInstance = CreateParentInstance();
        var parentWorkflow = CreateParentWorkflowWithMapping();
        _scriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Throws(new TaskCanceledException("downstream call timed out"));

        var result = await CreateService().ApplyAsync(
            parentInstance, parentWorkflow, "waiting-child", CreateChildData(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.SubflowOutputMappingFailed);
    }

    /// <summary>
    /// Mocks the whole fluent <see cref="IScriptContextBuilder"/> chain plus a real
    /// <see cref="ScriptContext"/>, mirroring <c>ResourceLockStepTests.SetupScriptContextFactory</c>,
    /// so the script-context build succeeds and execution reaches the compile step.
    /// </summary>
    private void SetupScriptContextFactory()
    {
        var builder = new Mock<IScriptContextBuilder>();
        builder.Setup(x => x.WithWorkflow(It.IsAny<Definitions.Workflow>())).Returns(builder.Object);
        builder.Setup(x => x.WithInstance(It.IsAny<Instance>())).Returns(builder.Object);
        builder.Setup(x => x.WithRuntime(It.IsAny<IRuntimeInfoProvider>())).Returns(builder.Object);
        builder.Setup(x => x.WithBody(It.IsAny<object>())).Returns(builder.Object);
        builder.Setup(x => x.BuildAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptContext(Mock.Of<ILogger<ScriptContext>>()));

        _scriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Returns(builder.Object);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ApplyPreparedAsync_ExposesCarriedHeadersToOutputHandler_OnlyWhenEnabled(bool expose, bool expectSeen)
    {
        var parentInstance = CreateParentInstance();
        var parentWorkflow = CreateParentWorkflowWithMapping();
        IReadOnlyDictionary<string, string?>? applied = null;
        var builder = new Mock<IScriptContextBuilder>();
        builder.Setup(x => x.WithWorkflow(It.IsAny<Definitions.Workflow>())).Returns(builder.Object);
        builder.Setup(x => x.WithInstance(It.IsAny<Instance>())).Returns(builder.Object);
        builder.Setup(x => x.WithRuntime(It.IsAny<IRuntimeInfoProvider>())).Returns(builder.Object);
        builder.Setup(x => x.WithBody(It.IsAny<object>())).Returns(builder.Object);
        builder.Setup(x => x.WithHeaders(It.IsAny<Dictionary<string, string?>?>()))
            .Callback<Dictionary<string, string?>?>(h => applied = h)
            .Returns(builder.Object);
        builder.Setup(x => x.BuildAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptContext(Mock.Of<ILogger<ScriptContext>>()));
        _scriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Returns(builder.Object);

        var policy = new CarriedHeaderPolicy(
            Options.Create(new SubFlowCarriedHeadersOptions { ExposeToOutputMapping = expose }),
            NullLogger<CarriedHeaderPolicy>.Instance);
        var service = CreateService(policy);
        var plan = (await service.PrepareAsync(parentInstance.Id, parentWorkflow, "waiting-child")).Value!;

        var result = await service.ApplyPreparedAsync(
            parentInstance, parentWorkflow, plan, CreateChildData(),
            new Dictionary<string, string?> { ["sub"] = "u1", ["authorization"] = "Bearer forged" },
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        if (expectSeen)
        {
            applied.ShouldNotBeNull();
            applied["sub"].ShouldBe("u1");
            applied.ContainsKey("authorization").ShouldBeFalse();
        }
        else
        {
            applied.ShouldBeNull();
        }
    }

    [Fact]
    public async Task ApplyAsync_FaultPath_ExposesCarriedHeadersToOutputHandler()
    {
        var parentInstance = CreateParentInstance();
        var parentWorkflow = CreateParentWorkflowWithMapping();
        Dictionary<string, string?>? applied = null;
        var builder = new Mock<IScriptContextBuilder>();
        builder.Setup(x => x.WithWorkflow(It.IsAny<Definitions.Workflow>())).Returns(builder.Object);
        builder.Setup(x => x.WithInstance(It.IsAny<Instance>())).Returns(builder.Object);
        builder.Setup(x => x.WithRuntime(It.IsAny<IRuntimeInfoProvider>())).Returns(builder.Object);
        builder.Setup(x => x.WithBody(It.IsAny<object>())).Returns(builder.Object);
        builder.Setup(x => x.WithHeaders(It.IsAny<Dictionary<string, string?>?>()))
            .Callback<Dictionary<string, string?>?>(h => applied = h)
            .Returns(builder.Object);
        builder.Setup(x => x.BuildAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptContext(Mock.Of<ILogger<ScriptContext>>()));
        _scriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Returns(builder.Object);
        var policy = new CarriedHeaderPolicy(
            Options.Create(new SubFlowCarriedHeadersOptions { ExposeToOutputMapping = true }),
            NullLogger<CarriedHeaderPolicy>.Instance);

        var result = await CreateService(policy).ApplyAsync(
            parentInstance, parentWorkflow, "waiting-child", CreateChildData(),
            CancellationToken.None,
            new Dictionary<string, string?> { ["sub"] = "u1" });

        result.IsSuccess.ShouldBeTrue();
        applied.ShouldNotBeNull();
        applied["sub"].ShouldBe("u1");
    }

    private SubflowOutputMappingService CreateService(ICarriedHeaderPolicy? policy = null)
        => new(
            _instanceRepository.Object,
            _instanceDataWriteService.Object,
            _scriptEngine.Object,
            _scriptContextFactory.Object,
            _runtimeInfoProvider.Object,
            _guidGenerator.Object,
            _logger.Object,
            policy);

    private static Instance CreateParentInstance()
        => Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");

    /// <summary>
    /// A parent workflow whose "waiting-child" SubFlow state carries a non-empty mapping, so the
    /// early-return guards at ApplyAsync:33 (state not found) and :38 (no mapping code) are cleared
    /// and execution reaches the script-context build the tests fail on.
    /// </summary>
    private static Definitions.Workflow CreateParentWorkflowWithMapping()
    {
        var state = StateFactory.CreateDefault("waiting-child", StateType.SubFlow);
        state.SetSubFlow(
            SubFlowType.SubFlow.Code,
            new Reference("child-flow", "bank", "sys-flows", "1.0.0"),
            ScriptCode.FromNative("return data;"),
            viewOverrides: null);

        var workflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        workflow.AddState(state);
        return workflow;
    }

    private static JsonElement CreateChildData()
    {
        using var document = JsonDocument.Parse("""{"result":"ok"}""");
        return document.RootElement.Clone();
    }
}
