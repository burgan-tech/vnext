using BBT.Aether.Results;
using BBT.Workflow.Agents;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step (order 65, issue #1105) that runs the agent of an <see cref="StateType.AiAgent"/> state
/// on entry. It does not request the next transition itself: it leaves the run's decision on
/// <see cref="TransitionExecutionContext.AgentDecision"/> and the automatic-transition step (order 80) picks
/// the winner, so there is a single path out of the state.
/// </summary>
/// <remarks>
/// Not applicable to updateData: the self-target profile excludes this step, and the guard below keeps
/// that true even if a profile changes. A hop that did not run the agent (updateData, a subflow resume
/// entering at order 79) carries no decision, and the automatic-transition step then leaves the agent's
/// candidates alone.
/// </remarks>
public sealed class RunAgentStep(
    AgentStateRunner runner,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.AgentRun;

    /// <inheritdoc />
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        if (context is not { Target: { StateType: StateType.AiAgent, Agent.Decision: not null }, Transition: not null } ||
            context.IsUpdateDataTransition())
        {
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        var scriptContext = await context.GetOrBuildScriptContextAsync(ct => BuildScriptContextAsync(context, ct), cancellationToken);
        var transitionId = context.Items.TryGetValue("TransitionRecordId", out var record) ? record as Guid? : null;

        var decision = await runner.RunAsync(context, scriptContext, transitionId, cancellationToken);
        if (!decision.IsSuccess)
            return Result<StepOutcome>.Fail(decision.Error);

        context.AgentDecision = decision.Value;
        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    private async Task<ScriptContext> BuildScriptContextAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        var builder = scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(context.Workflow)
            .WithInstance(context.Instance)
            .WithBody(context.Data)
            .WithRuntime(runtimeInfoProvider)
            .WithHeaders(context.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

        if (context.Transition != null)
            builder.WithTransition(context.Transition);

        return await builder.BuildAsync(cancellationToken);
    }
}
