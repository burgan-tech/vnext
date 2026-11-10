using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions;

/// <summary>
/// The per-hop log scope replaced a <c>Dictionary&lt;string, object&gt;</c>; OpenObserve queries key on
/// its entries, so the keys, values and order must be exactly what the dictionary carried.
/// </summary>
public sealed class TransitionLogScopeTests
{
    private static readonly string[] FixedKeys =
    [
        TelemetryConstants.TagNames.Domain,
        TelemetryConstants.TagNames.Flow,
        TelemetryConstants.TagNames.FlowVersion,
        TelemetryConstants.TagNames.InstanceId,
        TelemetryConstants.TagNames.InstanceKey,
        TelemetryConstants.TagNames.StateFrom,
        TelemetryConstants.TagNames.StateTo,
        TelemetryConstants.TagNames.TransitionKey,
        TelemetryConstants.TagNames.TriggerType,
        TelemetryConstants.TagNames.CorrelationId,
        "ChainDepth",
        "PipelineProfile",
    ];

    [Fact]
    public void RootInstance_CarriesTheTwelveFixedEntries_InDictionaryOrder()
    {
        var instanceId = Guid.NewGuid();
        var scope = Create(instanceId, parent: null, root: null);

        scope.Select(kv => kv.Key).ShouldBe(FixedKeys);
        var values = scope.ToDictionary(kv => kv.Key, kv => kv.Value);
        values[TelemetryConstants.TagNames.InstanceId].ShouldBe(instanceId);
        values["ChainDepth"].ShouldBe(3);
        values[TelemetryConstants.TagNames.StateFrom].ShouldBe("from");
        scope.Count.ShouldBe(12);
    }

    [Fact]
    public void SubflowInstance_AppendsParentThenRoot()
    {
        var parent = Guid.NewGuid();
        var root = Guid.NewGuid();
        var scope = Create(Guid.NewGuid(), parent, root);

        scope.Select(kv => kv.Key).ShouldBe(
            FixedKeys.Concat([TelemetryConstants.TagNames.ParentInstanceId, TelemetryConstants.TagNames.RootInstanceId]));
        scope[12].Value.ShouldBe(parent);
        scope[13].Value.ShouldBe(root);
    }

    [Fact]
    public void IsEnumerableAsTheKeyValueShapeLoggingProvidersRead()
    {
        object scope = Create(Guid.NewGuid(), null, Guid.NewGuid());

        (scope is IEnumerable<KeyValuePair<string, object>>).ShouldBeTrue();
        (scope is IReadOnlyList<KeyValuePair<string, object?>>).ShouldBeTrue();
        scope.ToString()!.ShouldContain($"{TelemetryConstants.TagNames.TransitionKey}:approve");
    }

    private static TransitionLogScope Create(Guid instanceId, Guid? parent, Guid? root) => new(
        "core", "flow", "1.0.0", instanceId, "key", "from", "to", "approve", "Manual", "corr", 3, "Manual",
        parent, root);
}
