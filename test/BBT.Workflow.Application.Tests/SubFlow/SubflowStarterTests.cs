using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public class SubflowStarterTests
{
    [Theory]
    [InlineData("S", ExecMode.Async)]
    [InlineData("S", ExecMode.Sync)]
    [InlineData("S", ExecMode.Resume)]
    [InlineData("P", ExecMode.Async)]
    [InlineData("P", ExecMode.Sync)]
    [InlineData("P", ExecMode.Resume)]
    public async Task StartAsync_ShouldAlwaysStartChildSynchronously(string type, ExecMode callerMode)
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var starter = new SubflowStarter(gateway, new ConfigurationBuilder().Build(),
            Substitute.For<IScriptEngine>(), Substitute.For<ILogger<SubflowStarter>>());
        var workflow = WorkflowFactory.CreateDefault();
        var parent = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent");
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow(type, new Reference("child-flow", "remote", "sys-flows", "1.0.0"), null!, null);
        var childId = Guid.NewGuid();
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), parent.Id, state.Key,
            childId, type, "remote", "child-flow", "1.0.0");
        gateway.StartSubAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput
            {
                Id = childId,
                Status = InstanceStatus.Active
            }));

        var result = await starter.StartAsync(workflow, parent, state,
            TransitionFactory.CreateDefault(), correlation, null!, callerMode);

        result.IsSuccess.ShouldBeTrue();
        await gateway.Received(1).StartSubAsync(
            Arg.Is<StartInstanceInput>(input => input.Sync && input.StrictIdempotency &&
                input.Instance.Id == childId && input.Domain == "remote"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// history: none (vnext#1006): a history-none parent stamps <c>parent.history</c> so the child can
    /// refuse to start unless it is history-none too; a flag-less parent's child metadata is unchanged.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartAsync_StampsParentHistory_OnlyForHistoryNoneParents(bool parentIsNone)
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var starter = new SubflowStarter(gateway, new ConfigurationBuilder().Build(),
            Substitute.For<IScriptEngine>(), Substitute.For<ILogger<SubflowStarter>>());
        var workflow = WorkflowFactory.CreateDefault();
        if (parentIsNone)
            workflow.SetHistory(HistoryMode.None);
        var parent = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent");
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow("S", new Reference("child-flow", "remote", "sys-flows", "1.0.0"), null!, null);
        var childId = Guid.NewGuid();
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), parent.Id, state.Key,
            childId, "S", "remote", "child-flow", "1.0.0");
        StartInstanceInput? captured = null;
        gateway.StartSubAsync(Arg.Do<StartInstanceInput>(i => captured = i), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput { Id = childId, Status = InstanceStatus.Completed }));

        await starter.StartAsync(workflow, parent, state, TransitionFactory.CreateDefault(), correlation, null!, ExecMode.Sync);

        captured.ShouldNotBeNull();
        captured!.Instance.ExtraProperties.ContainsKey(DomainConsts.MetaDataKeys.History).ShouldBe(parentIsNone);
        captured.Instance.ExtraProperties.RequiresHistoryNoneChild().ShouldBe(parentIsNone);
    }

    [Fact]
    public async Task StartAsync_SuppressesChildResponseEnrichment()
    {
        // The child's sync start response is read for IsSuccess only — the starter never looks at
        // attributes, ETag or extensions — so the child must not pay for projecting them.
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var starter = new SubflowStarter(gateway, new ConfigurationBuilder().Build(),
            Substitute.For<IScriptEngine>(), Substitute.For<ILogger<SubflowStarter>>());
        var workflow = WorkflowFactory.CreateDefault();
        var parent = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent");
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow("S", new Reference("child-flow", "remote", "sys-flows", "1.0.0"), null!, null);
        var childId = Guid.NewGuid();
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), parent.Id, state.Key,
            childId, "S", "remote", "child-flow", "1.0.0");
        StartInstanceInput? captured = null;
        gateway.StartSubAsync(Arg.Do<StartInstanceInput>(input => captured = input), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput { Id = childId, Status = InstanceStatus.Active }));

        var result = await starter.StartAsync(workflow, parent, state,
            TransitionFactory.CreateDefault(), correlation, null!, ExecMode.Sync);

        result.IsSuccess.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Sync.ShouldBeTrue();
        captured.SuppressResponseEnrichment.ShouldBeTrue();
        // The payload is the runtime's own input-mapping output, never a client body.
        captured.TrustedPayload.ShouldBeTrue();
    }

    /// <summary>
    /// The link between the starter and <see cref="InstanceType"/>: the parent block this starter
    /// builds is the only input the child's classification has, so the two must agree. Asserted here
    /// rather than in a domain test because only this side proves the block is actually produced —
    /// and that its flow type comes from the SubFlow definition, not from the child's own.
    /// </summary>
    [Theory]
    [InlineData("S")]
    [InlineData("P")]
    public async Task StartAsync_BuildsAParentBlockThatClassifiesTheChild(string type)
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var starter = new SubflowStarter(gateway, new ConfigurationBuilder().Build(),
            Substitute.For<IScriptEngine>(), Substitute.For<ILogger<SubflowStarter>>());
        var workflow = WorkflowFactory.CreateDefault();
        var parent = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent");
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow(type, new Reference("child-flow", "remote", "sys-flows", "1.0.0"), null!, null);
        var childId = Guid.NewGuid();
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), parent.Id, state.Key,
            childId, type, "remote", "child-flow", "1.0.0");
        StartInstanceInput? captured = null;
        gateway.StartSubAsync(Arg.Do<StartInstanceInput>(input => captured = input), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput { Id = childId, Status = InstanceStatus.Active }));

        var result = await starter.StartAsync(workflow, parent, state,
            TransitionFactory.CreateDefault(), correlation, null!, ExecMode.Sync);

        result.IsSuccess.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Instance.ExtraProperties[DomainConsts.MetaDataKeys.Id].ShouldBe(parent.Id);
        captured.Instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType].ShouldBe(type);
        InstanceType.FromStartMetadata(captured.Instance.ExtraProperties)
            .ShouldBe(InstanceType.FromCode(type));
    }

    // Instance-call credential rule: the mapping decides, blank included; only an absent credential
    // is filled from the caller's request headers.

    private static ScriptContext CallerContext() =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetHeaders(new Dictionary<string, string> { ["sub"] = "u1", ["act_sub"] = "a1", ["role"] = "maker" })
            .Build();

    private static async Task<StartInstanceInput> StartCapturingHeadersAsync(object? mappingHeaders, ScriptContext? caller)
    {
        var (result, _, captured) = await StartWithMappingHeadersAsync(mappingHeaders, caller);

        result.IsSuccess.ShouldBeTrue();
        captured.ShouldNotBeNull();
        return captured!;
    }

    private static async Task<(Result<string?> Result, IInstanceCommandGateway Gateway, StartInstanceInput? Captured)>
        StartWithMappingHeadersAsync(object? mappingHeaders, ScriptContext? caller)
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var engine = Substitute.For<IScriptEngine>();
        var mapping = Substitute.For<ISubFlowMapping>();
        mapping.InputHandler(Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new ScriptResponse { Headers = mappingHeaders }));
        engine.CompileToInstanceAsync<ISubFlowMapping>(
                Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings?>(),
                Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
                Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mapping));
        var starter = new SubflowStarter(gateway, new ConfigurationBuilder().Build(), engine,
            Substitute.For<ILogger<SubflowStarter>>());
        var workflow = WorkflowFactory.CreateDefault();
        var parent = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent");
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow("S", new Reference("child-flow", "remote", "sys-flows", "1.0.0"),
            ScriptCode.FromNative("// mapping"), null);
        var childId = Guid.NewGuid();
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), parent.Id, state.Key,
            childId, "S", "remote", "child-flow", "1.0.0");
        StartInstanceInput? captured = null;
        gateway.StartSubAsync(Arg.Do<StartInstanceInput>(i => captured = i), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput { Id = childId, Status = InstanceStatus.Active }));

        var result = await starter.StartAsync(workflow, parent, state, TransitionFactory.CreateDefault(),
            correlation, caller!, ExecMode.Sync);

        return (result, gateway, captured);
    }

    [Fact]
    public async Task NoMappingHeaders_CarryTheCallerCredential()
    {
        var input = await StartCapturingHeadersAsync(null, CallerContext());

        input.Headers["sub"].ShouldBe("u1");
        input.Headers["act_sub"].ShouldBe("a1");
        input.Headers["role"].ShouldBe("maker");
    }

    [Fact]
    public async Task BlankMappingCredential_IsCarriedAsAMarker()
    {
        var input = await StartCapturingHeadersAsync(
            new Dictionary<string, string?> { ["sub"] = "" }, CallerContext());

        input.Headers["sub"].ShouldBe("");
        input.Headers["act_sub"].ShouldBe("a1");
    }

    [Fact]
    public async Task MappingCredential_Wins()
    {
        var input = await StartCapturingHeadersAsync(
            new Dictionary<string, string?> { ["sub"] = "other" }, CallerContext());

        input.Headers["sub"].ShouldBe("other");
    }

    [Fact]
    public async Task ExpandoMappingHeaders_AreHonoured()
    {
        dynamic headers = new ExpandoObject();
        headers.sub = "";

        var input = await StartCapturingHeadersAsync((object)headers, CallerContext());

        input.Headers["sub"].ShouldBe("");
    }

    [Fact]
    public async Task AnonymousObjectMappingHeaders_AreHonoured()
    {
        var input = await StartCapturingHeadersAsync(new { sub = "" }, CallerContext());

        input.Headers["sub"].ShouldBe("");
        input.Headers["act_sub"].ShouldBe("a1");
    }

    public static TheoryData<object> UnreadableHeaderShapes => new()
    {
        "sub=",
        42,
        new[] { "sub" },
    };

    [Theory]
    [MemberData(nameof(UnreadableHeaderShapes))]
    public async Task UnreadableMappingHeaders_FailTheStart_AndStartNoChild(object mappingHeaders)
    {
        var (result, gateway, _) = await StartWithMappingHeadersAsync(mappingHeaders, CallerContext());

        result.IsSuccess.ShouldBeFalse();
        await gateway.DidNotReceive().StartSubAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoCaller_AddsNothing()
    {
        var input = await StartCapturingHeadersAsync(
            new Dictionary<string, string?> { ["x-foo"] = "1" }, null);

        input.Headers["x-foo"].ShouldBe("1");
        input.Headers.ContainsKey(TelemetryConstants.HeaderNames.ParentInstanceId).ShouldBeTrue();
        input.Headers.ContainsKey(TelemetryConstants.HeaderNames.RootInstanceId).ShouldBeTrue();
        input.Headers.ContainsKey("sub").ShouldBeFalse();
    }

    [Fact]
    public async Task ParentAndRootIds_AreStillFrameworkStamped()
    {
        var input = await StartCapturingHeadersAsync(
            new Dictionary<string, string?> { [TelemetryConstants.HeaderNames.ParentInstanceId] = "evil" }, null);

        input.Headers[TelemetryConstants.HeaderNames.ParentInstanceId]
            .ShouldBe(input.Instance.ExtraProperties[DomainConsts.MetaDataKeys.Id]!.ToString());
    }
}
