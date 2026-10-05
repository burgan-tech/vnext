using System;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// <c>context.Incident</c> as a mapping sees it. A script context always holds a
/// <see cref="Instance.CreateSnapshot"/>, and a snapshot carries every loaded incident in its
/// DETACHED list, never on the EF navigation. Reading the navigation therefore gave a context that
/// said <c>HasActiveIncident = true</c> with <c>ActiveIncident = null</c> and a count of 0 — the
/// shape a domain mapping saw inside the error-boundary transition that handles a subflow fault.
/// </summary>
public class ScriptContextIncidentTests
{
    [Fact]
    public void SetInstance_OnASnapshot_ExposesTheOpenIncident()
    {
        var (instance, incident) = InstanceWithOpenIncident();

        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance.CreateSnapshot())
            .Build();

        AssertExposes(context, incident);
    }

    [Fact]
    public void RefreshInstance_ExposesTheOpenIncident()
    {
        var (instance, incident) = InstanceWithOpenIncident();
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "other").CreateSnapshot())
            .Build();

        context.RefreshInstance(instance);

        AssertExposes(context, incident);
    }

    [Fact]
    public void ParallelBranch_ExposesTheOpenIncident()
    {
        var (instance, incident) = InstanceWithOpenIncident();
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance.CreateSnapshot())
            .Build();

        AssertExposes(context.CreateParallelBranch(), incident);
    }

    [Fact]
    public void AResolvedIncident_IsNotTheActiveOne()
    {
        var (instance, _) = InstanceWithOpenIncident();
        instance.ResolveOpenIncidents();

        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance.CreateSnapshot())
            .Build();

        context.Incident!.HasActiveIncident.ShouldBeFalse();
        context.Incident.ActiveIncident.ShouldBeNull();
        context.Incident.TotalIncidentCount.ShouldBe(1);
    }

    private static (Instance Instance, InstanceIncident Incident) InstanceWithOpenIncident()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "with-incident");
        var incident = InstanceIncidentFactory.Create(
            state: "in-subflow", transition: "call", taskKey: "http-call",
            message: "SubFlow 'child' faulted: HTTP BadRequest", errorCode: "SubFlow:Faulted:Task:Http:http-call:400",
            errorLayer: "SubFlow", statusCode: 400, boundaryAction: "Notify", boundaryLevel: "Global");
        instance.AddIncident(incident);
        instance.MarkIncidentsLoaded();
        return (instance, incident);
    }

    private static void AssertExposes(ScriptContext context, InstanceIncident expected)
    {
        context.Incident.ShouldNotBeNull();
        context.Incident!.HasActiveIncident.ShouldBeTrue();
        context.Incident.TotalIncidentCount.ShouldBe(1);
        context.Incident.ActiveIncident.ShouldNotBeNull();
        context.Incident.ActiveIncident!.Id.ShouldBe(expected.Id);
        context.Incident.ActiveIncident.StatusCode.ShouldBe(400);
    }
}
