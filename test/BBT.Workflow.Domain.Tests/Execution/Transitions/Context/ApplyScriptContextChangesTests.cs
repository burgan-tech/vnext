using System;
using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transitions.Context;

/// <summary>
/// Unit tests for <see cref="TransitionExecutionContext.ApplyScriptContextChanges"/> under the
/// immediate-persist model: task outputs are persisted by the write service the moment they are
/// produced, so this method no longer replays data — it only syncs the LIVE aggregate's
/// in-memory latest with the snapshot's freshest persisted row (needed when a parallel-branch
/// scope wrote through its own DbContext) and applies the non-data mutations.
/// </summary>
public class ApplyScriptContextChangesTests
{
    [Fact]
    public void Apply_SnapshotHasNewerPersistedRow_SyncsItIntoTheLiveAggregate()
    {
        var live = CreateInstanceWithLatest("1.2.1");
        var scriptContext = CreateScriptContextSnapshot(live);

        // A branch-scope persist landed on the snapshot: a REAL persisted row (identity assigned).
        var persisted = new InstanceData(
            Guid.NewGuid(), live.Id, "1.2.2", new JsonData("{\"task\":\"result\"}"), true)
        {
            VersionNo = 2
        };
        scriptContext.Instance!.AcceptPersistedData(persisted);

        var context = CreateContext(live);
        context.ApplyScriptContextChanges(scriptContext);

        live.LatestData!.Id.ShouldBe(persisted.Id);
        live.DataList.Count(d => d.IsLatest).ShouldBe(1);
    }

    [Fact]
    public void Apply_RowAlreadyInLiveAggregate_DoesNotDuplicate()
    {
        // Sequential path: EF fixup already attached the persisted row to the live aggregate.
        var live = CreateInstanceWithLatest("1.2.1");
        var scriptContext = CreateScriptContextSnapshot(live);
        var countBefore = live.DataList.Count;

        var context = CreateContext(live);
        context.ApplyScriptContextChanges(scriptContext);
        context.ApplyScriptContextChanges(scriptContext);

        live.DataList.Count.ShouldBe(countBefore);
    }

    [Fact]
    public void Apply_OnLatestOnlyLoadedAggregate_DoesNotThrow()
    {
        var live = CreateInstanceWithLatest("1.2.1");
        live.MarkDataPartiallyLoaded();
        var scriptContext = CreateScriptContextSnapshot(live);
        scriptContext.Instance!.AcceptPersistedData(new InstanceData(
            Guid.NewGuid(), live.Id, "1.2.2", new JsonData("{\"task\":1}"), true)
        {
            VersionNo = 2
        });

        var context = CreateContext(live);

        Should.NotThrow(() => context.ApplyScriptContextChanges(scriptContext));
        live.LatestData!.Version.ShouldBe("1.2.2");
    }

    private static Instance CreateInstanceWithLatest(string version)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0");
        instance.AcceptPersistedData(new InstanceData(
            Guid.NewGuid(), instance.Id, version, new JsonData("{\"seed\":true}"), true)
        {
            VersionNo = 1
        });
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
