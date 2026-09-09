using System;
using System.Linq;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the aggregate-side contract of the incident child collection: the denormalized
/// <see cref="Instance.HasActiveIncident"/> flag, the pending-row bookkeeping the repository relies
/// on, and the fail-fast guard against resolving incidents that were never loaded.
/// </summary>
public class InstanceIncidentTests
{
    private static InstanceIncident CreateIncident(string errorCode = "Task:Http:503") =>
        InstanceIncidentFactory.Create(
            state: "review", transition: "submit", taskKey: "call-service",
            message: "boom", errorCode: errorCode, errorLayer: "Task");

    [Fact]
    public void AddIncident_Unresolved_RaisesFlagAndTracksPendingRow()
    {
        var instance = InstanceFactory.CreateDefault();
        var incident = CreateIncident();

        instance.AddIncident(incident);

        instance.HasActiveIncident.ShouldBeTrue();
        incident.InstanceId.ShouldBe(instance.Id);
        instance.GetPendingIncidents().ShouldBe([incident]);
        instance.GetLoadedIncidents().ShouldBe([incident]);
    }

    [Fact]
    public void AddIncident_AlreadyResolved_DoesNotRaiseFlag()
    {
        // Log/Ignore boundary actions record an informational incident that is resolved before it is
        // attached — it must be persisted but must not mark the instance as needing attention.
        var instance = InstanceFactory.CreateDefault();
        var incident = CreateIncident();
        incident.Resolve();

        instance.AddIncident(incident);

        instance.HasActiveIncident.ShouldBeFalse();
        instance.GetPendingIncidents().Count.ShouldBe(1);
    }

    [Fact]
    public void AddIncident_NeverPrunes_HistoryIsUnbounded()
    {
        var instance = InstanceFactory.CreateDefault();
        for (var i = 0; i < 12; i++)
        {
            var incident = CreateIncident($"code-{i}");
            incident.Resolve();
            instance.AddIncident(incident);
        }

        instance.GetLoadedIncidents().Count.ShouldBe(12);
    }

    [Fact]
    public void ResolveOpenIncidents_ResolvesEveryOpenRowAndClearsFlag()
    {
        // One failure can leave more than one open row (a job-timeout recovery on top of a boundary
        // incident, a boundary transition that faults on its own, a parent taking a subflow fault
        // while it already carries one). Resolving only the newest left the flag stuck true on an
        // instance that had recovered and completed.
        var instance = InstanceFactory.CreateDefault();
        var older = CreateIncident("older");
        var newer = CreateIncident("newer");
        instance.AddIncident(older);
        instance.AddIncident(newer);

        var resolved = instance.ResolveOpenIncidents();

        resolved.ShouldBe([older, newer]);
        older.IsResolved.ShouldBeTrue();
        newer.IsResolved.ShouldBeTrue();
        instance.HasActiveIncident.ShouldBeFalse();

        instance.ResolveOpenIncidents().ShouldBeEmpty();
        instance.HasActiveIncident.ShouldBeFalse();
    }

    [Fact]
    public void ResolveOpenIncidents_LeavesAlreadyResolvedRowsUntouched()
    {
        var instance = InstanceFactory.CreateDefault();
        var informational = CreateIncident("informational");
        informational.Resolve();
        var resolvedAt = informational.ResolvedAt;
        instance.AddIncident(informational);
        var open = CreateIncident("open");
        instance.AddIncident(open);

        var resolved = instance.ResolveOpenIncidents();

        resolved.ShouldBe([open]);
        informational.ResolvedAt.ShouldBe(resolvedAt, "an already-resolved incident is not re-stamped");
    }

    [Fact]
    public void ResolveOpenIncidents_WhenFlagSetButIncidentsNotLoaded_Throws()
    {
        // Simulates an aggregate materialized from the database: the flag column says true, but the
        // child rows were not included. Resolving must not silently do nothing.
        var instance = InstanceFactory.CreateDefault();
        instance.AddIncident(CreateIncident());
        var reloaded = SimulateReloadWithoutIncidents(instance);

        Should.Throw<InvalidOperationException>(() => reloaded.ResolveOpenIncidents())
            .Message.ShouldContain("LoadActiveIncidentsAsync");
    }

    [Fact]
    public void AcceptLoadedIncidents_MarksLoadedAndDeduplicates()
    {
        var instance = InstanceFactory.CreateDefault();
        var pending = CreateIncident("pending");
        instance.AddIncident(pending);
        var fromDb = CreateIncident("from-db");

        instance.AcceptLoadedIncidents([fromDb, pending]);

        instance.IncidentsLoaded.ShouldBeTrue();
        instance.GetLoadedIncidents().Select(i => i.ErrorCode).ShouldBe(["pending", "from-db"], ignoreOrder: true);
        instance.GetLoadedIncidents().Count.ShouldBe(2);
    }

    [Fact]
    public void MarkIncidentsLoaded_AllowsResolveOnFlaggedAggregate()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.AddIncident(CreateIncident());
        var reloaded = SimulateReloadWithoutIncidents(instance);

        reloaded.AcceptLoadedIncidents([CreateIncident("db-row")]);

        var resolved = reloaded.ResolveOpenIncidents();
        resolved.ShouldNotBeEmpty();
        reloaded.HasActiveIncident.ShouldBeFalse();
    }

    [Fact]
    public void ClearPendingIncidents_ForgetsPendingButKeepsLoaded()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.AddIncident(CreateIncident());

        instance.ClearPendingIncidents();

        instance.GetPendingIncidents().ShouldBeEmpty();
        instance.GetLoadedIncidents().Count.ShouldBe(1);
    }

    [Fact]
    public void Unfault_ResolvesEveryOpenIncidentAndClearsFlag()
    {
        // Two open rows is the shape a recovered instance used to get stuck on: the retry resolved
        // one and the flag stayed true on an instance that went on to complete successfully.
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(StateFactory.CreateDefault("review", StateType.Intermediate, StateSubType.None));
        instance.AddIncident(CreateIncident("first"));
        instance.AddIncident(CreateIncident("second"));
        instance.Fault("test-domain");
        instance.HasActiveIncident.ShouldBeTrue();

        instance.Unfault().ShouldBeTrue();

        instance.Status.ShouldBe(InstanceStatus.Active);
        instance.HasActiveIncident.ShouldBeFalse();
        instance.GetLoadedIncidents().ShouldAllBe(i => i.IsResolved);
    }

    [Fact]
    public void CreateSnapshot_CopiesFlagLoadedMarkerAndIncidents()
    {
        var instance = InstanceFactory.CreateDefault();
        var incident = CreateIncident();
        instance.AddIncident(incident);
        instance.MarkIncidentsLoaded();

        var snapshot = instance.CreateSnapshot();

        snapshot.HasActiveIncident.ShouldBeTrue();
        snapshot.IncidentsLoaded.ShouldBeTrue();
        snapshot.GetLoadedIncidents().ShouldBe([incident]);
        snapshot.GetPendingIncidents().ShouldBeEmpty("a snapshot is never persisted");
    }

    /// <summary>
    /// Produces an aggregate that looks like a fresh database load: same flag, no incident rows,
    /// not marked loaded. Uses the snapshot API and strips the shared incident list by re-creating.
    /// </summary>
    private static Instance SimulateReloadWithoutIncidents(Instance source)
    {
        var reloaded = Instance.Create(source.Id, source.Flow, source.FlowVersion, source.Key);
        // Raise the flag the way a persisted row would carry it, without attaching any incident.
        var marker = CreateIncident("marker");
        reloaded.AddIncident(marker);
        reloaded.ClearPendingIncidents();
        // Remove the marker's visibility by resolving it through a fresh flag-only aggregate:
        // the flag is what a load would restore; the list must be empty. Rebuild via snapshot of an
        // instance whose only incident was added then dropped is not possible through the public API,
        // so we emulate the persisted shape with a private helper.
        return FlagOnly(reloaded);
    }

    private static Instance FlagOnly(Instance flagged)
    {
        var fresh = Instance.Create(flagged.Id, flagged.Flow, flagged.FlowVersion, flagged.Key);
        typeof(Instance).GetProperty(nameof(Instance.HasActiveIncident))!
            .SetValue(fresh, true);
        return fresh;
    }
}
