using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.ReEntry;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Orchestrates transition chaining with isolated DI scope and UoW per hop.
/// Each transition runs in its own scope with RequiresNew UoW for complete isolation.
/// This ensures deterministic post-commit behavior for inline auto chain processing.
/// Applies Global Error Boundary for errors that escape State-level handling.
/// </summary>
public sealed class TransitionRunner(
    IServiceScopeFactory scopeFactory,
    IComponentCacheStore componentCacheStore,
    IErrorPolicyResolver errorPolicyResolver,
    IErrorActionExecutor errorActionExecutor,
    IErrorNormalizer errorNormalizer,
    IOptions<ReentryOptions> options,
    ILogger<TransitionRunner> logger) : ITransitionRunner
{
    /// <inheritdoc />
    /// <summary>
    /// Runs a transition in its own DI scope + RequiresNew UoW.
    /// Sync dispatch chain for auto transitions is managed by TransitionPipeline.
    /// </summary>
    public async Task<Result<TransitionOutput>> RunAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // Execute in isolated scope + UoW
        var hopResult = await ExecuteWithScopeAsync(context, cancellationToken);
        if (!hopResult.IsSuccess)
            return Result<TransitionOutput>.Fail(hopResult.Error);

        var coreOutput = hopResult.Value!;
        return Result<TransitionOutput>.Ok(coreOutput.Output);
    }

    /// <summary>
    /// Executes the transition in a new DI scope with RequiresNew UoW.
    /// This ensures complete isolation from any ambient UoW.
    /// Applies Global Error Boundary for errors that escape State-level handling.
    /// </summary>
    private async Task<Result<TransitionCoreOutput>> ExecuteWithScopeAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var core = sp.GetRequiredService<IWorkflowExecutionCore>();

        await using var uow = await uowManager.BeginAsync(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
            cancellationToken);

        var coreResult = await core.ExecuteTransitionCoreAsync(context, cancellationToken);

        if (!coreResult.IsSuccess)
        {
            // Apply Global Error Boundary for errors that escaped State-level handling
            var globalResult = await ApplyGlobalErrorBoundaryAsync(
                context,
                coreResult.Error,
                cancellationToken);

            if (!globalResult.IsSuccess)
            {
                // Global boundary also failed or no policy found - propagate original error
                return Result<TransitionCoreOutput>.Fail(coreResult.Error);
            }

            // Global boundary handled the error
            var actionResult = globalResult.Value;
            if (actionResult != null && actionResult.HasTransition)
            {
                // Global error policy requested a transition - this will be handled in the next hop
                logger.LogInformation(
                    "Global error boundary triggered transition to {TransitionKey}",
                    actionResult.TransitionKey);
            }

            // If global boundary allows continuation, return empty output
            // (the actual state hasn't changed, so we can't return a meaningful output)
            if (actionResult?.ShouldContinue == true)
            {
            }

            return Result<TransitionCoreOutput>.Fail(coreResult.Error);
        }

        // Commit is THE boundary
        await uow.CommitAsync(cancellationToken);

        return coreResult;
    }

    /// <summary>
    /// Applies Global Error Boundary for errors that escaped State-level handling.
    /// Loads workflow definition and resolves global error policy.
    /// </summary>
    private async Task<Result<ErrorActionResult?>> ApplyGlobalErrorBoundaryAsync(
        WorkflowExecutionContext context,
        Error error,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Applying Global Error Boundary for instance {InstanceId}. Error: {ErrorCode} - {ErrorMessage}",
            context.InstanceId,
            error.Code,
            error.Message);

        // Load workflow definition for global error boundary resolution
        var workflowResult = await componentCacheStore.GetFlowAsync(
            context.Domain,
            context.WorkflowKey,
            context.WorkflowVersion,
            cancellationToken);

        if (!workflowResult.IsSuccess)
        {
            logger.LogError(
                "Failed to load workflow {WorkflowKey} for global error boundary resolution",
                context.WorkflowKey);
            return Result<ErrorActionResult?>.Fail(error);
        }

        var workflow = workflowResult.Value!;

        // Check if workflow has global error boundary
        if (workflow.ErrorBoundary == null)
        {
            logger.LogDebug(
                "Workflow {WorkflowKey} has no global error boundary defined",
                context.WorkflowKey);
            return Result<ErrorActionResult?>.Ok(null);
        }

        // Build minimal transition context for policy resolution
        var transitionContext = new TransitionExecutionContext
        {
            Domain = context.Domain,
            InstanceId = context.InstanceId != null ? Guid.Parse(context.InstanceId) : Guid.Empty,
            WorkflowKey = context.WorkflowKey,
            Workflow = workflow
        };

        // Build error context
        var errorContext = ErrorContextBuilder.Create(errorNormalizer)
            .WithError(error)
            .WithScope(ErrorBoundaryScope.Global)
            .FromContext(transitionContext)
            .Build();

        // Resolve global error policy
        var resolvedPolicy = errorPolicyResolver.Resolve(transitionContext, errorContext, onExecuteTask: null);

        if (resolvedPolicy == null)
        {
            logger.LogWarning(
                "No global error policy matched for error {ErrorCode} in workflow {WorkflowKey}",
                error.Code,
                context.WorkflowKey);
            return Result<ErrorActionResult?>.Ok(null);
        }

        // Execute error action
        var actionResult = await errorActionExecutor.ExecuteAsync(
            transitionContext,
            errorContext,
            resolvedPolicy,
            cancellationToken);

        logger.LogInformation(
            "Global error boundary applied action {Action}. Continue={Continue}, Transition={Transition}",
            actionResult.Action,
            actionResult.ShouldContinue,
            actionResult.TransitionKey);

        return Result<ErrorActionResult?>.Ok(actionResult);
    }
}

