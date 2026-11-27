using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Authorization.RowSecurity;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions.CastHandlers;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// A workflow or function <c>output</c> mixing a script (<c>location</c>/<c>code</c>) with a declarative block
/// (<c>select</c>/<c>into</c>/<c>set</c>) fails to deserialize. On the publish path that surfaces as a validation
/// failure (HTTP 400 class) naming <c>output</c>, never as a 500 and never as a silently stored script.
/// </summary>
public sealed class DefinitionAppServiceDeclarativeOutputTests
{
    private const string MixedOutput =
        """{ "location": "./src/Output.csx", "code": "cmV0dXJuIHRydWU7", "set": { "$.data.x": "1" } }""";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly DefinitionAppService _sut;

    public DefinitionAppServiceDeclarativeOutputTests()
    {
        var guids = Substitute.For<IGuidGenerator>();
        guids.Create().Returns(_ => Guid.NewGuid());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<ILazyServiceProvider, LazyServiceProvider>();
        services.AddSingleton(guids);
        services.AddSingleton(Substitute.For<ICurrentSchema>());
        services.AddSingleton(Substitute.For<ISchemaMigrationOrchestrator>());
        var provider = services.BuildServiceProvider();

        var castHandler = Substitute.For<IWorkflowCastHandler>();
        castHandler.CanHandle(Arg.Any<string>()).Returns(true);

        _sut = new DefinitionAppService(
            Substitute.For<ICurrentSchema>(),
            Substitute.For<IRuntimeInfoProvider>(),
            _instanceRepository,
            Substitute.For<IInstanceDataWriteService>(),
            new ComponentValidatorProcessor(
                [new FlowComponentValidator(new WorkflowValidator()), new FunctionComponentValidator()],
                Substitute.For<IScriptAssemblyCatalog>()),
            new WorkflowCastProcessor([castHandler]),
            Substitute.For<IDomainCacheContext>(),
            new RowSecurityPublishCheck(Substitute.For<IComponentCacheStore>(), NullLogger<RowSecurityPublishCheck>.Instance),
            new MappingBlockValidator(new ExpressionCompiler(16)),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider);
    }

    private static PublishInput Input(string flow, string json) => new()
    {
        Key = "out-a",
        Domain = "test-domain",
        Flow = flow,
        FlowVersion = "1.0.0",
        Version = "1.0.0",
        Attributes = JsonDocument.Parse(json).RootElement.Clone()
    };

    private static string Flow(string output) => $$"""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                { "key": "review", "stateType": 2, "versionStrategy": "Minor",
                  "labels": [{"label": "Review", "language": "en"}], "transitions": [] }
            ],
            "sharedTransitions": [],
            "startTransition": { "key": "start", "target": "review", "versionStrategy": "Minor", "triggerType": 0,
                "labels": [{"label": "Start", "language": "en"}] },
            "output": {{output}}
        }
        """;

    private static string Function(string output) => $$"""
        {
            "scope": "F",
            "onExecutionTasks": [
                { "order": 1, "task": {"key": "t", "domain": "d", "flow": "sys-tasks", "version": "1.0.0"} }
            ],
            "output": {{output}}
        }
        """;

    private async Task ShouldRejectMixedOutput(PublishInput input)
    {
        var result = await _sut.PublishAsync(input);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.Validation);
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InvalidWorkflow);
        result.Error.ValidationErrors.ShouldNotBeNull();
        result.Error.ValidationErrors!.ShouldContain(e => e.ErrorMessage!.Contains("'output'"));
        await _instanceRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
    }

    [Fact]
    public Task Workflow_MixedOutputShape_IsPublishError() =>
        ShouldRejectMixedOutput(Input(RuntimeSysSchemaInfo.Flows, Flow(MixedOutput)));

    [Fact]
    public Task Function_MixedOutputShape_IsPublishError() =>
        ShouldRejectMixedOutput(Input(RuntimeSysSchemaInfo.Functions, Function(MixedOutput)));

    [Fact]
    public async Task Workflow_BlockOutput_Publishes()
    {
        var result = await _sut.PublishAsync(Input(RuntimeSysSchemaInfo.Flows,
            Flow("""{ "select": "$.data.customer", "into": "$.data" }""")));

        result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.ReceivedWithAnyArgs(1).InsertAsync(default!, default, default);
    }
}
