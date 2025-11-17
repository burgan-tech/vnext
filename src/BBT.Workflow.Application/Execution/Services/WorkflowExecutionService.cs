using BBT.Aether.DistributedLock;
using BBT.Workflow.Domain;
using BBT.Workflow.Domain.Extensions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Validation;
using BBT.Workflow.Schemas;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Service implementation for orchestrating workflow execution operations using the new pipeline architecture.
/// Coordinates between validation, preparation, handlers, and execution strategies.
/// </summary>
public sealed class WorkflowExecutionService(
    IExecutionStrategyFactory execFactory,
    ICurrentSchema currentSchema,
    IDistributedLockService distributedLockService,
    IInstanceRepository instanceRepository,
    IInstanceDataValidationService instanceDataValidationService,
    ILogger<WorkflowExecutionService> logger) : IWorkflowExecutionService
{
    /// <summary>
    /// Executes a workflow transition using the new pipeline architecture.
    /// Exception handling is delegated to the middleware layer for consistent error responses.
    /// </summary>
    /// <param name="context">The workflow execution input containing all necessary data.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A Result containing the transition output.</returns>
    /// <exception cref="WorkflowValidationException">Thrown when validation fails</exception>
    /// <exception cref="AutoTransitionConditionNotMetException">Thrown when auto-transition condition is not met</exception>
    /// <exception cref="DistributedLockAcquisitionException">Thrown when lock cannot be acquired</exception>
    public async Task<Result<TransitionOutput>> ExecuteTransitionAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        using (currentSchema.Change(context.WorkflowKey))
        {
            // Enrich all logs with comprehensive workflow context for distributed tracing
            using (logger.ForTransition(
                domain: context.Domain,
                flow: context.WorkflowKey,
                flowVersion: context.WorkflowVersion,
                instanceId: context.InstanceId,
                transitionKey: context.TransitionKey))
            {
                var resourceId = $"instance-{context.InstanceId}";

                // 1. Get execution strategy
                var strategy = execFactory.Get(context.Mode);
                
                // 2. Execute with distributed lock
                // Exceptions are propagated to middleware for consistent error handling
                var transitionExecutionContext = await distributedLockService.ExecuteWithLockAsync(
                    resourceId,
                    InstanceConstants.TransitionLockExpiryInSeconds,
                    async () =>
                    {
                        // Execute strategy
                        var executionResult = await strategy.ExecuteAsync(context, cancellationToken);
                        
                        // If strategy failed, throw appropriate exception for middleware to handle
                        if (!executionResult.IsSuccess)
                        {
                            ThrowAppropriateException(executionResult.Error);
                        }
                        
                        var execContext = executionResult.Value!;
                        
                        // Validate instance data after transition execution (PostExecution mode)
                        var validationResult = await instanceDataValidationService.ValidatePostExecutionAsync(
                            execContext.Instance,
                            execContext.Workflow,
                            logger,
                            cancellationToken);
                        
                        if (!validationResult.IsSuccess)
                        {
                            logger.LogWarning(
                                "Instance data validation failed after transition execution for instance {InstanceId}: {ErrorCode}",
                                execContext.InstanceId, validationResult.Error.Code);
                            ThrowAppropriateException(validationResult.Error);
                        }
                        
                        return execContext;
                    },
                    cancellationToken,
                    logger);
                
                sw.Stop();
                
                // Log completion with structured data
                logger.TransitionCompleted(
                    TelemetryConstants.Prefixes.Application,
                    context.TransitionKey,
                    context.InstanceId,
                    sw.ElapsedMilliseconds);
            
                // Build response
                TransitionOutput response;
                if (transitionExecutionContext.ClientResponse is not null)
                {
                    response = new TransitionOutput
                    {
                        Id = transitionExecutionContext.InstanceId,
                        Status = transitionExecutionContext.ClientResponse.Status
                    };
                }
                else
                {
                    var freshInstance = await instanceRepository.FindByIdAsReadOnlyAsync(
                        context.InstanceId, 
                        cancellationToken);

                    response = new TransitionOutput
                    {
                        Id = context.InstanceId,
                        Status = freshInstance!.Status
                    };
                }

                return Result<TransitionOutput>.Ok(response);
            }
        }
    }
    
    /// <summary>
    /// Throws the appropriate exception based on the error code.
    /// This allows the middleware to handle different error types consistently.
    /// </summary>
    private static void ThrowAppropriateException(Error error)
    {
        // For auto-transition condition not met, throw specific exception
        if (error.Code == WorkflowErrorCodes.AutoTransitionConditionNotMet)
        {
            throw new AutoTransitionConditionNotMetException();
        }
        
        // For validation errors, throw WorkflowValidationException
        if (error.ValidationErrors is { Count: > 0 })
        {
            throw new WorkflowValidationException(error);
        }
        
        // For other errors, throw InvalidOperationException with error code embedded
        // This will be handled by the middleware's backward compatibility logic
        throw new InvalidOperationException($"{error.Code}:{error.Message}");
    }
}