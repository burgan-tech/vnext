using System;
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

    [Fact]
    public async Task ApplyAsync_WhenScriptContextBuildFailsTransiently_ShouldRethrowRatherThanReturnFailure()
    {
        var parentInstance = CreateParentInstance();
        var parentWorkflow = CreateParentWorkflowWithMapping();
        _scriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Throws(new FileLoadException("Assembly with same name is already loaded"));

        await Should.ThrowAsync<FileLoadException>(
            () => CreateService().ApplyAsync(
                parentInstance, parentWorkflow, "waiting-child", CreateChildData(), CancellationToken.None));
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

    private SubflowOutputMappingService CreateService()
        => new(
            _instanceRepository.Object,
            _instanceDataWriteService.Object,
            _scriptEngine.Object,
            _scriptContextFactory.Object,
            _runtimeInfoProvider.Object,
            _guidGenerator.Object,
            _logger.Object);

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
