using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.ReEntry;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly ReentryOptions _options = options.Value;

    /// <inheritdoc />
    /// <summary>
    /// Runs transitions in a loop, each in its own DI scope + RequiresNew UoW.
    /// Post-commit: enqueues inline auto chain transitions from DirectivesSnapshot.
    /// </summary>
    [Trace]
    public async Task<Result<TransitionOutput>> RunAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var queue = new Queue<WorkflowExecutionContext>();
        queue.Enqueue(context);

        var hop = 0;
        TransitionOutput? lastOutput = null;

        while (queue.TryDequeue(out var current))
        {
            hop++;

            // Guard: prevent infinite loops
            if (hop > _options.MaxAutoHops)
            {
                if (Guid.TryParse(current.InstanceId, out var instanceGuid))
                {
                    logger.MaxAutoHopsExceeded(_options.MaxAutoHops, instanceGuid, current.Execution?.ExecutionChainId);
                }
                return Result<TransitionOutput>.Fail(
                    Error.Validation("auto.chain.maxhops", $"Auto chain exceeded max hops: {_options.MaxAutoHops}"));
            }

            // Execute in isolated scope + UoW
            var hopResult = await ExecuteHopAsync(current, cancellationToken);
            if (!hopResult.IsSuccess)
                return Result<TransitionOutput>.Fail(hopResult.Error);

            var coreOutput = hopResult.Value!;
            lastOutput = coreOutput.Output;

            // POST-COMMIT: enqueue inline auto chain transitions
            if (coreOutput.DirectivesSnapshot.HasQueuedTransitions)
            {
                foreach (var cmd in coreOutput.DirectivesSnapshot.InlineAutoQueue)
                {
                    // Increment chain depth for the next hop
                    var nextCmd = cmd with { ChainDepth = cmd.ChainDepth + 1 };
                    
                    // Guard: check chain depth before enqueueing
                    if (nextCmd.ChainDepth <= _options.MaxAutoHops)
                    {
                        queue.Enqueue(WorkflowExecutionContext.From(nextCmd));
                    }
                    else
                    {
                        logger.MaxAutoHopsExceeded(_options.MaxAutoHops, nextCmd.InstanceId, nextCmd.ExecutionChainId);
                    }
                }
            }
        }

        return lastOutput is null
            ? Result<TransitionOutput>.Fail(Error.Failure("transition.output.missing", "Transition output missing"))
            : Result<TransitionOutput>.Ok(lastOutput);
    }

    /// <summary>
    /// Executes a single transition hop in a new DI scope with RequiresNew UoW.
    /// This ensures complete isolation from any ambient UoW.
    /// Applies Global Error Boundary for errors that escape State-level handling.
    /// </summary>
    private async Task<Result<TransitionCoreOutput>> ExecuteHopAsync(
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

        // Commit is THE boundary - post-commit processing happens after this
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

