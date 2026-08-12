using System;
using System.Linq;
using BBT.Aether.DependencyInjection;
using BBT.Workflow.DefinitionContext;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the no-change dedup in <see cref="Instance.AddData"/>: the stored row is the
/// FULL merged state, so the comparison must run on the merged result. A delta input whose merge
/// produces byte-identical content (an idempotent duplicate callback re-stamping an already-set
/// key) must NOT create a new version — that is exactly what <c>DataHash</c> exists for.
/// </summary>
public class InstanceDataMergedDedupTests
{
    public InstanceDataMergedDedupTests()
    {
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
    public void AddData_DeltaProducingNoChange_ReturnsExistingRowWithoutNewVersion()
    {
        var instance = CreateInstance();
        instance.AddData(Guid.NewGuid(), new JsonData("{\"a\":1,\"rr_doc1\":true}"), VersionStrategy.IncreasePatch);
        var head = instance.LatestData!;
        var countBefore = instance.DataList.Count;

        // Idempotent duplicate: the key is already set — merged content is identical.
        var result = instance.AddData(Guid.NewGuid(), new JsonData("{\"rr_doc1\":true}"), VersionStrategy.IncreaseMinor);

        result.ShouldBeSameAs(head);
        instance.DataList.Count.ShouldBe(countBefore);
        instance.LatestData!.Version.ShouldBe(head.Version);
    }

    [Fact]
    public void AddData_DeltaProducingChange_CreatesMergedNewVersion()
    {
        var instance = CreateInstance();
        instance.AddData(Guid.NewGuid(), new JsonData("{\"a\":1}"), VersionStrategy.IncreasePatch);
        var countBefore = instance.DataList.Count;

        var result = instance.AddData(Guid.NewGuid(), new JsonData("{\"b\":2}"), VersionStrategy.IncreasePatch);

        instance.DataList.Count.ShouldBe(countBefore + 1);
        result.IsLatest.ShouldBeTrue();
        // Full-merge model: the stored row carries the complete state, not the delta.
        result.Data.Json.ShouldContain("\"a\"");
        result.Data.Json.ShouldContain("\"b\"");
    }

    [Fact]
    public void AddData_IdenticalFullInput_StillDedups()
    {
        var instance = CreateInstance();
        var first = instance.AddData(Guid.NewGuid(), new JsonData("{\"a\":1}"), VersionStrategy.IncreasePatch);
        var countBefore = instance.DataList.Count;

        var result = instance.AddData(Guid.NewGuid(), new JsonData("{\"a\":1}"), VersionStrategy.IncreasePatch);

        result.ShouldBeSameAs(first);
        instance.DataList.Count.ShouldBe(countBefore);
    }

    [Fact]
    public void AddData_ValueChangeOnExistingKey_CreatesNewVersion()
    {
        var instance = CreateInstance();
        instance.AddData(Guid.NewGuid(), new JsonData("{\"count\":1}"), VersionStrategy.IncreasePatch);
        var countBefore = instance.DataList.Count;

        var result = instance.AddData(Guid.NewGuid(), new JsonData("{\"count\":2}"), VersionStrategy.IncreasePatch);

        instance.DataList.Count.ShouldBe(countBefore + 1);
        result.Data.Json.ShouldContain("2");
    }

    private static Instance CreateInstance()
        => Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0");
}
