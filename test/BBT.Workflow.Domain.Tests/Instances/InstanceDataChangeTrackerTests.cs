using System;
using System.Linq;
using BBT.Aether.DependencyInjection;
using BBT.Workflow.DefinitionContext;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BBT.Workflow.Instances;

public sealed class InstanceDataChangeTrackerTests : DomainTestBase<DomainEntryPoint>
{
    public InstanceDataChangeTrackerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowContext>(new NullWorkflowContext());
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    [Fact]
    public void Tracked_snapshot_should_record_only_successful_new_AddData_inputs_in_order()
    {
        var instance = CreateWithData("{\"base\":1}");
        instance.MarkDataPartiallyLoaded();
        var snapshot = instance.CreateTrackedDataSnapshot();

        var firstId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        snapshot.AddData(firstId, JsonData.CreateFrom("{\"left\":2}"), VersionStrategy.IncreasePatch);
        snapshot.AddData(duplicateId, JsonData.CreateFrom("{\"base\":1,\"left\":2}"), VersionStrategy.IncreasePatch);
        snapshot.AddData(secondId, JsonData.CreateFrom("{\"right\":3}"), VersionStrategy.IncreaseMinor);

        var changes = Assert.IsType<InstanceDataChangeSet>(snapshot.GetPendingDataChangeSet());
        Assert.True(snapshot.IsDataPartiallyLoaded);
        Assert.Equal(instance.LatestData!.Id, changes.Baseline!.DataId);
        Assert.Equal(new[] { firstId, secondId }, changes.Contributions.Select(x => x.DataId));
        Assert.Equal(new[] { 0, 1 }, changes.Contributions.Select(x => x.Order));
        Assert.Equal(
            new[] { VersionStrategy.IncreasePatch, VersionStrategy.IncreaseMinor },
            changes.Contributions.Select(x => x.VersionStrategy));
        Assert.Equal("{\"base\":1,\"left\":2,\"right\":3}", snapshot.LatestData!.Data.Json);
    }

    [Fact]
    public void Ordinary_snapshot_should_not_create_a_change_set()
    {
        var snapshot = CreateWithData("{\"value\":1}").CreateSnapshot();
        snapshot.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"value\":2}"));

        Assert.Null(snapshot.GetPendingDataChangeSet());
    }

    [Fact]
    public void Acknowledge_should_clear_entries_and_advance_baseline()
    {
        var snapshot = CreateWithData("{\"value\":1}").CreateTrackedDataSnapshot();
        var persisted = snapshot.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"value\":2}"));

        snapshot.AcknowledgeDataChanges(persisted);

        Assert.Null(snapshot.GetPendingDataChangeSet());
        snapshot.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"value\":3}"));
        Assert.Equal(persisted.Id, snapshot.GetPendingDataChangeSet()!.Baseline!.DataId);
    }

    [Fact]
    public void Empty_tracked_snapshot_should_not_create_a_change_set_and_should_keep_a_null_baseline_for_its_first_contribution()
    {
        var snapshot = InstanceFactory.CreateDefault().CreateTrackedDataSnapshot();

        Assert.Null(snapshot.GetPendingDataChangeSet());

        snapshot.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"value\":1}"));

        var changes = Assert.IsType<InstanceDataChangeSet>(snapshot.GetPendingDataChangeSet());
        Assert.Null(changes.Baseline);
        Assert.Single(changes.Contributions);
    }

    [Fact]
    public void SynchronizePartiallyLoadedData_should_preserve_the_loaded_head_and_add_each_persisted_row_once()
    {
        var instance = CreateWithData("{\"value\":1}");
        instance.MarkDataPartiallyLoaded();
        var snapshot = instance.CreateTrackedDataSnapshot();
        var loadedHead = snapshot.LatestData!;
        var persistedLoadedHead = InstanceData.Rehydrate(snapshot.Id, new InstanceDataHead(
            loadedHead.Id,
            loadedHead.ETag,
            loadedHead.Version,
            loadedHead.VersionNo,
            loadedHead.HistorySequence,
            loadedHead.DataHash,
            loadedHead.Data,
            loadedHead.EnteredAt));
        persistedLoadedHead.MarkAsNotLatest();
        var persistedLatest = new InstanceData(
            Guid.NewGuid(),
            snapshot.Id,
            "1.0.1",
            JsonData.CreateFrom("{\"value\":2}"),
            true,
            loadedHead.HistorySequence + 1)
        {
            VersionNo = loadedHead.VersionNo + 1
        };
        var persistedData = new[] { persistedLoadedHead, persistedLatest };

        snapshot.SynchronizePartiallyLoadedData(persistedData);
        snapshot.SynchronizePartiallyLoadedData(persistedData);

        Assert.Same(loadedHead, Assert.Single(snapshot.DataList, x => x.Id == loadedHead.Id));
        Assert.False(loadedHead.IsLatest);
        Assert.Single(snapshot.DataList, x => x.Id == persistedLatest.Id);
        Assert.Single(snapshot.DataList, x => x.IsLatest);
        Assert.Same(persistedLatest, snapshot.LatestData);
        Assert.True(snapshot.IsDataPartiallyLoaded);
    }

    private static Instance CreateWithData(string json)
    {
        var instance = InstanceFactory.CreateDefault();
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom(json));
        return instance;
    }

    private sealed class NullWorkflowContext : IWorkflowContext
    {
        public Definitions.Workflow? Workflow => null;
        public bool HasWorkflow => false;

        public void SetWorkflow(Definitions.Workflow workflow)
        {
        }
    }
}
