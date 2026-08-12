using System;
using System.Linq;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Test-only seeding of instance data rows. Production writes go exclusively through
/// <c>IInstanceDataWriteService</c> (immediate persistence, identity computed under the row
/// lock); tests seed the equivalent in-memory end state here — a fully-identified row accepted
/// via <see cref="Instance.AcceptPersistedData"/> — without a database. Signatures mirror the
/// removed <c>Instance.AddData</c> / <c>AddDataWithVersion</c> so call sites stay readable.
/// </summary>
public static class InstanceDataSeeder
{
    /// <summary>
    /// Seeds a strategy append: merges the delta into the current latest (full-merge model),
    /// bumps the version by the strategy, and accepts the row as the new latest.
    /// </summary>
    public static InstanceData SeedData(
        this Instance instance,
        Guid id,
        JsonData inputData,
        VersionStrategy? versionStrategy = null)
    {
        var head = instance.LatestData;
        var content = head is null ? inputData : head.Data.Merge(inputData);
        var version = head is null
            ? WorkflowConstants.DefaultVersion
            : InstanceData.IncrementVersion(head.Version, versionStrategy ?? VersionStrategy.None);

        return instance.SeedRow(id, version, content, isLatest: true);
    }

    /// <summary>
    /// Seeds an explicit-version row: takes the latest flag only when the version compares at
    /// or above the current head (an older line never steals the global latest).
    /// </summary>
    public static InstanceData SeedDataWithVersion(
        this Instance instance,
        Guid id,
        JsonData inputData,
        string version)
    {
        var head = instance.LatestData;
        var takesLatest = head is null
            || InstanceDataVersionComparer.CompareVersionStrings(version, head.Version) >= 0;

        return instance.SeedRow(id, version, inputData, takesLatest);
    }

    private static InstanceData SeedRow(
        this Instance instance,
        Guid id,
        string version,
        JsonData content,
        bool isLatest)
    {
        var historySequence = instance.DataList
            .Where(d => d.Version == version)
            .Select(d => d.HistorySequence)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        var row = new InstanceData(id, instance.Id, version, content, isLatest, historySequence)
        {
            VersionNo = instance.DataList.Select(d => d.VersionNo).DefaultIfEmpty(0L).Max() + 1
        };

        instance.AcceptPersistedData(row);
        return row;
    }
}
