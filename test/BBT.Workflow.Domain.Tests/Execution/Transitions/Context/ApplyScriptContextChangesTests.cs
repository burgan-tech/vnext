using System;
using System.Linq;
using BBT.Aether.DependencyInjection;
using BBT.Workflow.Definitions;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transitions.Context;

/// <summary>
/// Unit tests for <see cref="TransitionExecutionContext.ApplyScriptContextChanges"/>: task
/// results captured on the detached ScriptContext snapshot are replayed onto the live
/// aggregate BY STRATEGY — the version is recomputed from the live head instead of carrying
/// the snapshot's frozen version string, which could sit below the live head after a
/// concurrent append (throwing on latest-only aggregates or demoting a newer head).
/// </summary>
public class ApplyScriptContextChangesTests
{
    public ApplyScriptContextChangesTests()
    {
        // The [SchemaValidation] aspect woven into Instance.AddData/AddDataWithVersion resolves
        // its services from the ambient (AsyncLocal) provider; a workflow-less context makes
        // validation a no-op (same pattern as InstanceDataLatestInvariantTests).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowContext>(new NullWorkflowContext());
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    private sealed class NullWorkflowContext : IWorkflowContext
    {
        public Definitions.Workflow? Workflow => null;
        public bool HasWorkflow => false;
        public void SetWorkflow(Definitions.Workflow workflow)
        {
        }
    }

    [Fact]
    public void Apply_WhenLiveHeadAdvancedPastSnapshot_RecomputesAboveLiveHead()
    {
        var live = CreateInstanceWithData("1.2.1");
        var scriptContext = CreateScriptContextSnapshot(live);

        // Task output lands on the snapshot: 1.2.1 -> 1.2.2 (patch).
        scriptContext.Instance!.AddData(
            Guid.NewGuid(), new JsonData("{\"task\":\"result\"}"), VersionStrategy.IncreasePatch);

        // Meanwhile the live aggregate advanced (e.g. the transition's own payload write).
        live.AddData(Guid.NewGuid(), new JsonData("{\"payload\":true}"), VersionStrategy.IncreaseMinor);
        var liveHead = live.LatestData!.Version; // 1.3.0

        var context = CreateContext(live);
        context.ApplyScriptContextChanges(scriptContext);

        // The replayed row is recomputed ABOVE the live head — never the frozen 1.2.2.
        var applied = live.DataList.OrderByDescending(d => d, InstanceDataVersionComparer.Instance).First();
        applied.Version.ShouldBe("1.3.1");
        InstanceDataVersionComparer.CompareVersionStrings(applied.Version, liveHead).ShouldBeGreaterThan(0);
        applied.IsLatest.ShouldBeTrue();
        live.DataList.Count(d => d.IsLatest).ShouldBe(1);
    }

    [Fact]
    public void Apply_OnLatestOnlyLoadedAggregate_DoesNotThrow()
    {
        // The production shape: GetActiveAsync loads latest-only and marks the aggregate
        // partial. Replaying a stale snapshot version used to throw
        // "the aggregate was loaded latest-only and the target version line is not in memory".
        var live = CreateInstanceWithData("1.2.1");
        live.MarkDataPartiallyLoaded();

        var scriptContext = CreateScriptContextSnapshot(live);
        scriptContext.Instance!.AddData(
            Guid.NewGuid(), new JsonData("{\"task\":\"result\"}"), VersionStrategy.IncreasePatch);

        live.AddData(Guid.NewGuid(), new JsonData("{\"payload\":true}"), VersionStrategy.IncreaseMinor);

        var context = CreateContext(live);

        Should.NotThrow(() => context.ApplyScriptContextChanges(scriptContext));
        live.LatestData!.Version.ShouldBe("1.3.1");
    }

    [Fact]
    public void Apply_SameRowTwice_IsIdempotent()
    {
        var live = CreateInstanceWithData("1.0.0");
        var scriptContext = CreateScriptContextSnapshot(live);
        scriptContext.Instance!.AddData(
            Guid.NewGuid(), new JsonData("{\"task\":1}"), VersionStrategy.IncreasePatch);

        var context = CreateContext(live);
        context.ApplyScriptContextChanges(scriptContext);
        var countAfterFirst = live.DataList.Count;

        context.ApplyScriptContextChanges(scriptContext);

        live.DataList.Count.ShouldBe(countAfterFirst);
    }

    private static Instance CreateInstanceWithData(string seedVersion)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), new JsonData("{\"seed\":true}"), seedVersion);
        return instance;
    }

    private static ScriptContext CreateScriptContextSnapshot(Instance live)
    {
        var scriptContext = new ScriptContext(NullLogger<ScriptContext>.Instance);
        scriptContext.RefreshInstance(live);
        return scriptContext;
    }

    private static TransitionExecutionContext CreateContext(Instance instance) => new()
    {
        InstanceId = instance.Id,
        Domain = "test-domain",
        WorkflowKey = instance.Flow,
        TransitionKey = "test-transition",
        Trigger = TriggerType.Manual,
        CorrelationId = Guid.NewGuid().ToString("N"),
        ExecutionChainId = Guid.NewGuid().ToString("N"),
        RequestedAt = DateTimeOffset.UtcNow,
        Workflow = Definitions.Workflow.Create(),
        Current = StateFactory.CreateDefault("current"),
        Transition = Transition.Create("test-transition", "current", "current", TriggerType.Manual, "Patch"),
        Instance = instance,
        TraceId = Guid.NewGuid().ToString("N"),
        SpanId = Guid.NewGuid().ToString("N")[..16]
    };
}
