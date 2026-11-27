using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Shared;
using BBT.Workflow.SubFlow;
using BBT.Workflow.SubFlow.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// A SubFlow state's declarative <c>output</c> block (Phase C1): it runs where the <c>mapping</c> script's OutputHandler
/// runs, on both the completion and the fault path. Its delta starts empty; <c>select</c> reads under
/// <c>result = { data, status }</c> (default <c>$.data</c>); the delta is appended with the same single
/// <see cref="IInstanceDataWriteService.AppendAsync"/> the script result uses, and an empty delta appends nothing.
/// </summary>
public sealed class SubflowOutputDeclarativeTests
{
    private const string StateKey = "waiting-child";

    private readonly IInstanceRepository _repository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceDataWriteService _writer = Substitute.For<IInstanceDataWriteService>();
    private readonly IScriptEngine _engine = Substitute.For<IScriptEngine>();
    private readonly IScriptContextFactory _contextFactory = Substitute.For<IScriptContextFactory>();
    private readonly CapturingLogger _logger = new();
    private readonly Instance _parent = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
    private readonly List<string> _appended = [];
    private Dictionary<string, string?>? _scriptHeaders;
    private ICallerAccessor? _scriptCaller;

    public SubflowOutputDeclarativeTests()
    {
        _writer.AppendAsync(Arg.Any<Instance>(), Arg.Do<JsonData>(d => _appended.Add(d.Json)),
                Arg.Any<VersionStrategy?>(), Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<InstanceData?>(null));

        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithWorkflow(Arg.Any<Definitions.Workflow>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance>()).Returns(builder);
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithBody(Arg.Any<object>()).Returns(builder);
        builder.WithHeaders(Arg.Do<Dictionary<string, string?>?>(h => _scriptHeaders = h)).Returns(builder);
        builder.WithCaller(Arg.Do<ICallerAccessor?>(c => _scriptCaller = c)).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetInstance(_parent)
                .SetHeaders(_scriptHeaders)
                .SetCaller(_scriptCaller)
                .Build();
            return Task.FromResult(context);
        });
        _contextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);
    }

    [Fact]
    public async Task Completed_DefaultSelectWritesChildData()
    {
        var service = CreateService();
        var workflow = Workflow(Block(into: "$.child", set: [("$.childStatus", "result.status")]));

        var result = await ApplyAsync(service, workflow, ChildData(), SubflowSettlement.Completed);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _appended.ShouldHaveSingleItem().ShouldBe("""{"child":{"result":"ok"},"childStatus":"completed"}""");
    }

    [Fact]
    public async Task Faulted_StatusIsFaulted()
    {
        var service = CreateService();
        var workflow = Workflow(Block(set: [("$.childStatus", "result.status")]));

        var result = await ApplyAsync(service, workflow, ChildData(), SubflowSettlement.Faulted);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _appended.ShouldHaveSingleItem().ShouldBe("""{"childStatus":"faulted"}""");
    }

    [Fact]
    public async Task FaultWithoutChildData_WritesNothing()
    {
        var service = CreateService();
        var workflow = Workflow(Block(into: "$.child"));

        var result = await ApplyAsync(service, workflow, childData: null, SubflowSettlement.Faulted);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _appended.ShouldBeEmpty();
        await _writer.DidNotReceiveWithAnyArgs().AppendAsync(default!, default!, default, default, default, default);
    }

    [Theory]
    [InlineData(true, """{"seen":"u1"}""")]
    [InlineData(false, null)]
    public async Task HeadersHiddenWhenPolicyDoesNotExpose(bool expose, string? expected)
    {
        var service = CreateService(Policy(expose));
        var workflow = Workflow(Block(set: [("$.seen", "headers[\"sub\"]")]));

        var result = await ApplyAsync(service, workflow, ChildData(), SubflowSettlement.Completed, IdentityHeaders());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        if (expected is null)
            _appended.ShouldBeEmpty();
        else
            _appended.ShouldHaveSingleItem().ShouldBe(expected);
    }

    [Fact]
    public async Task UserReadTriggersWarmup()
    {
        var resolver = ResolvingResolver("approver");
        var service = CreateService(Policy(expose: true), new CallerAccessorFactory(resolver, NullLogger<CallerAccessorFactory>.Instance));
        var plan = (await service.PrepareAsync(_parent.Id, Workflow(Block(set: [("$.ok", "user.hasRole(\"approver\")")])), StateKey)).Value!;

        await service.WarmCallerRolesAsync(_parent.Id, plan, IdentityHeaders());

        await resolver.ReceivedWithAnyArgs(1).ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task NoUserRead_DoesNotWarm()
    {
        var resolver = ResolvingResolver("approver");
        var service = CreateService(Policy(expose: true), new CallerAccessorFactory(resolver, NullLogger<CallerAccessorFactory>.Instance));
        var plan = (await service.PrepareAsync(_parent.Id, Workflow(Block(set: [("$.s", "result.status")])), StateKey)).Value!;

        await service.WarmCallerRolesAsync(_parent.Id, plan, IdentityHeaders());

        await resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task UserRead_SeesTheChildSettlersRoles()
    {
        var service = CreateService(Policy(expose: true),
            new CallerAccessorFactory(ResolvingResolver("approver"), NullLogger<CallerAccessorFactory>.Instance));
        var workflow = Workflow(Block(set: [("$.ok", "user.hasRole(\"approver\")")]));

        var result = await ApplyAsync(service, workflow, ChildData(), SubflowSettlement.Completed, IdentityHeaders());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _appended.ShouldHaveSingleItem().ShouldBe("""{"ok":true}""");
    }

    [Fact]
    public async Task RolesUnavailable_OnCompletion_IsTransient()
    {
        var service = CreateService(Policy(expose: true),
            new CallerAccessorFactory(FailingResolver(), NullLogger<CallerAccessorFactory>.Instance));
        var workflow = Workflow(Block(set: [("$.ok", "user.hasRole(\"approver\")")]));

        await Should.ThrowAsync<CallerRolesUnavailableTransientException>(
            ApplyAsync(service, workflow, ChildData(), SubflowSettlement.Completed, IdentityHeaders()));

        _appended.ShouldBeEmpty();
    }

    [Fact]
    public async Task RolesUnavailable_Warmup_IsTransient()
    {
        var service = CreateService(Policy(expose: true),
            new CallerAccessorFactory(FailingResolver(), NullLogger<CallerAccessorFactory>.Instance));
        var plan = (await service.PrepareAsync(_parent.Id, Workflow(Block(set: [("$.ok", "user.roles.Length")])), StateKey)).Value!;

        await Should.ThrowAsync<CallerRolesUnavailableTransientException>(
            service.WarmCallerRolesAsync(_parent.Id, plan, IdentityHeaders()));
    }

    [Fact]
    public async Task FailingExpression_FailsLikeAFailingScript()
    {
        var service = CreateService();
        var workflow = Workflow(Block(set: [("$.x", "int.Parse(\"not-a-number\")")]));

        var result = await ApplyAsync(service, workflow, ChildData(), SubflowSettlement.Completed);

        result.IsSuccess.ShouldBeFalse();
        var expected = WorkflowErrors.SubFlowOutputMappingFailed(_parent.Id, "x");
        result.Error.Code.ShouldBe(expected.Code);
        result.Error.Message!.ShouldStartWith($"SubFlow output mapping failed for parent instance '{_parent.Id}': ");
        result.Error.Message.ShouldContain($"state '{StateKey}' declarative output mapping failed");
        _appended.ShouldBeEmpty();
        _logger.Entries.ShouldContain(e => e.EventId == 10279 && e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task OutputBlock_RunsUnderTheSubflowOutputMappingSpan()
    {
        using var spans = new BBT.Workflow.Application.Tests.Telemetry.TraceSpanCollector("BBT.Workflow.Scripting");

        (await ApplyAsync(CreateService(), Workflow(Block(set: [("$.x", "int.Parse(\"bad\")")])), ChildData(),
            SubflowSettlement.Completed)).IsSuccess.ShouldBeFalse();

        var span = spans.Spans.Where(a => a.DisplayName == "Script.Execute").ShouldHaveSingleItem();
        span.GetTagItem(TelemetryConstants.TagNames.ScriptKind).ShouldBe("subflowOutputMapping");
        span.Status.ShouldBe(System.Diagnostics.ActivityStatusCode.Error);
    }

    [Fact]
    public async Task BothScriptAndBlock_RunsScript()
    {
        var mapping = Substitute.For<ISubFlowMapping>();
        mapping.OutputHandler(Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new ScriptResponse { Data = new Dictionary<string, object> { ["fromScript"] = true } }));
        _engine.CompileToInstanceAsync<object>(
                Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings?>(),
                Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
                Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<object>(mapping));

        var result = await ApplyAsync(CreateService(),
            Workflow(Block(set: [("$.fromBlock", "true")]), withScript: true), ChildData(), SubflowSettlement.Completed);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _appended.ShouldHaveSingleItem().ShouldBe("""{"fromScript":true}""");
    }

    private Task<BBT.Aether.Results.Result> ApplyAsync(
        SubflowOutputMappingService service, Definitions.Workflow workflow, JsonElement? childData,
        SubflowSettlement settlement, IReadOnlyDictionary<string, string?>? carried = null) =>
        service.ApplyAsync(_parent, workflow, StateKey, childData, settlement, CancellationToken.None, carried);

    private SubflowOutputMappingService CreateService(
        ICarriedHeaderPolicy? policy = null, ICallerAccessorFactory? callerFactory = null) =>
        new(_repository, _writer, _engine, _contextFactory, Substitute.For<IRuntimeInfoProvider>(), _logger,
            policy, callerFactory);

    private static CarriedHeaderPolicy Policy(bool expose) =>
        new(Options.Create(new SubFlowCarriedHeadersOptions { ExposeToOutputMapping = expose }),
            NullLogger<CarriedHeaderPolicy>.Instance);

    private static Definitions.Workflow Workflow(MappingBlock block, bool withScript = false)
    {
        var state = StateFactory.CreateDefault(StateKey, StateType.SubFlow);
        state.SetSubFlow(SubFlowType.SubFlow.Code, new Reference("child-flow", "bank", "sys-flows", "1.0.0"),
            withScript ? ScriptCode.FromNative("// mapping") : null!, viewOverrides: null);
        typeof(Definitions.SubFlow).GetProperty(nameof(Definitions.SubFlow.Output))!.SetValue(state.SubFlow, block);
        var workflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        workflow.AddState(state);
        return workflow;
    }

    private static MappingBlock Block(string? select = null, string? into = null, (string Path, string Expr)[]? set = null) =>
        new(select, into, set is null
            ? null
            : new OrderedStringMap(set.Select(e => new KeyValuePair<string, string>(e.Path, e.Expr))));

    private static JsonElement ChildData()
    {
        using var document = JsonDocument.Parse("""{"result":"ok"}""");
        return document.RootElement.Clone();
    }

    private static Dictionary<string, string?> IdentityHeaders() => new() { ["sub"] = "u1", ["act_sub"] = "u1" };

    private static ICallerRoleResolver ResolvingResolver(params string[] roles)
    {
        var resolver = Substitute.For<ICallerRoleResolver>();
        resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CallerRoleResolution(roles, CallerRoleOutcome.Resolved)));
        return resolver;
    }

    private static ICallerRoleResolver FailingResolver()
    {
        var resolver = Substitute.For<ICallerRoleResolver>();
        resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CallerRoleResolution([], CallerRoleOutcome.Failed, "timeout")));
        return resolver;
    }

    private sealed class CapturingLogger : ILogger<SubflowOutputMappingService>
    {
        public List<(int EventId, LogLevel Level)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, logLevel));
    }
}
