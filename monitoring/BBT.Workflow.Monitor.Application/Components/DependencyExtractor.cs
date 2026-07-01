using BBT.Workflow.Definitions;
using BBT.Workflow.Monitor.Components.DTOs;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Monitor.Components;

/// <summary>Pure extraction of all component references from a workflow definition. No I/O.</summary>
public static class DependencyExtractor
{
    /// <summary>Extracts every component dependency from the given workflow definition.</summary>
    public static MonitorDependencyResponse Extract(WorkflowDefinition flow)
    {
        var deps = new MonitorDependencies();

        if (flow.Schema is { } wfSchema)
            deps.Schemas.Add(Ref(wfSchema.Key, wfSchema.Version, wfSchema.Domain, "workflow"));

        foreach (var f in flow.Functions)
            deps.Functions.Add(Ref(f.Key, f.Version, f.Domain, "workflow"));

        foreach (var e in flow.Extensions)
            deps.Extensions.Add(Ref(e.Key, e.Version, e.Domain, "workflow"));

        foreach (var s in flow.States)
        {
            AddTasks(deps, s.OnEntries, $"state:{s.Key}/onEntries");
            AddTasks(deps, s.OnExits, $"state:{s.Key}/onExits");
            AddViews(deps, s.View, $"state:{s.Key}");
            if (s.SubFlow is { } sf)
                deps.SubFlows.Add(Ref(sf.Process.Key, sf.Process.Version, sf.Process.Domain, $"state:{s.Key}/subFlow"));
            foreach (var t in s.Transitions)
                AddTransition(deps, t);
        }

        foreach (var t in flow.SharedTransitions)
            AddTransition(deps, t);

        return new MonitorDependencyResponse
        {
            Workflow     = new MonitorComponentRef { Key = flow.Key, Version = flow.Version, Domain = flow.Domain },
            Dependencies = deps
        };
    }

    private static void AddTransition(MonitorDependencies d, Transition t)
    {
        AddTasks(d, t.OnExecutionTasks, $"transition:{t.Key}");
        AddViews(d, t.View, $"transition:{t.Key}");
        if (t.Schema is { } sc)
            d.Schemas.Add(Ref(sc.Key, sc.Version, sc.Domain, $"transition:{t.Key}"));
    }

    private static void AddTasks(MonitorDependencies d, IEnumerable<OnExecuteTask> tasks, string from)
    {
        foreach (var ot in tasks)
            d.Tasks.Add(Ref(ot.Task.Key, ot.Task.Version, ot.Task.Domain, from));
    }

    private static void AddViews(MonitorDependencies d, ViewDefinition? viewDef, string from)
    {
        if (viewDef is null) return;
        foreach (var ve in viewDef.Views)
        {
            if (ve.View is { } v)
                d.Views.Add(Ref(v.Key, v.Version, v.Domain, from));
        }
    }

    private static MonitorDependencyRef Ref(string? key, string? version, string? domain, string from)
        => new() { Key = key, Version = version, Domain = domain, ReferencedFrom = from };
}
