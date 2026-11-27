using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Rules;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Maps an execution <see cref="ScriptContext"/> to the allowlisted <see cref="ExpressoRuleContext"/> for Dynamic Expresso.
/// </summary>
public static class ExpressoRuleContextMapper
{
    /// <summary>
    /// Builds a read-only rule context: Body, CurrentTransition, MetaData, Workflow, Instance, Headers,
    /// QueryParameters, RouteValues, Transition, and Runtime.
    /// </summary>
    public static ExpressoRuleContext FromScriptContext(ScriptContext scriptContext)
    {
        ArgumentNullException.ThrowIfNull(scriptContext);

        return new ExpressoRuleContext
        {
            Body = RuleJsonDynamic.FromObject(scriptContext.Body),
            Headers = RuleJsonDynamic.FromObject(scriptContext.Headers),
            QueryParameters = RuleJsonDynamic.FromObject(scriptContext.QueryParameters),
            RouteValues = RuleJsonDynamic.FromObject(scriptContext.RouteValues),
            MetaData = RuleJsonDynamic.FromObject(scriptContext.MetaData),
            CurrentTransition = MapCurrentTransition(scriptContext),
            Workflow = MapWorkflow(scriptContext.Workflow),
            Instance = MapInstance(scriptContext.Instance),
            Transition = MapTransition(scriptContext.Transition),
            Runtime = MapRuntime(scriptContext.Runtime)
        };
    }

    private static ExpressoCurrentTransitionView? MapCurrentTransition(ScriptContext scriptContext)
    {
        var ct = scriptContext.CurrentTransition;
        if (ct == null)
            return null;

        return new ExpressoCurrentTransitionView
        {
            Data = RuleJsonDynamic.FromObject(ct.Data),
            Header = RuleJsonDynamic.FromObject(ct.Header)
        };
    }

    private static ExpressoWorkflowView? MapWorkflow(WorkflowDefinition? workflow)
    {
        if (workflow == null)
            return null;

        return new ExpressoWorkflowView
        {
            Key = workflow.Key,
            Domain = workflow.Domain,
            Flow = workflow.Flow,
            Version = workflow.Version,
            StateKeys = workflow.States.Select(s => s.Key).ToList()
        };
    }

    private static ExpressoTransitionView? MapTransition(Transition? transition)
    {
        if (transition == null)
            return null;

        return new ExpressoTransitionView
        {
            Key = transition.Key,
            From = transition.From,
            Target = transition.Target,
            TriggerType = transition.TriggerType.ToString(),
            TriggerKind = transition.TriggerKind?.ToString()
        };
    }

    private static ExpressoRuntimeView? MapRuntime(IRuntimeInfoProvider? runtime)
    {
        if (runtime == null)
            return null;

        return new ExpressoRuntimeView
        {
            Domain = runtime.Domain,
            Version = runtime.Version
        };
    }

    /// <summary>
    /// Builds the read-only instance view (identity, state, status, latest data). Also the <c>instance</c>
    /// variable of an expression scope, which passes <paramref name="borrowData"/> so the latest data is
    /// wrapped with <see cref="RuleJsonDynamic.Borrow"/> instead of cloned.
    /// </summary>
    internal static ExpressoInstanceView? MapInstance(Instance? instance, bool borrowData = false)
    {
        if (instance == null)
            return null;

        var latest = instance.LatestData;
        RuleJsonDynamic data;
        if (latest == null)
            data = RuleJsonDynamic.Empty; // the same empty object the former undisposed Parse("{}") produced
        else
            data = borrowData
                ? RuleJsonDynamic.Borrow(latest.Data.JsonElement)
                : RuleJsonDynamic.FromJsonElement(latest.Data.JsonElement);

        return new ExpressoInstanceView
        {
            Id = instance.Id,
            Key = instance.Key,
            Flow = instance.Flow,
            Version = instance.FlowVersion,
            CurrentState = instance.CurrentState,
            EffectiveState = instance.EffectiveState,
            EffectiveStateType = instance.EffectiveStateType?.ToString(),
            EffectiveStateSubType = instance.EffectiveStateSubType?.ToString(),
            EffectiveStatus = instance.GetEffectiveStatus.Code,
            Type = instance.Type.Code,
            Data = data
        };
    }
}
