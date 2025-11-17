using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Aether.Guids;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Instances;
using BBT.Workflow.Tasks.Persistence;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using BBT.Workflow.Instances.Validation;

namespace BBT.Workflow.Tasks.Execution;

/// <summary>
/// Local implementation of IWorkflowTaskExecutor that executes tasks directly without remote calls.
/// This is used in the Execution service where tasks are executed locally.
/// </summary>
/// <param name="taskExecutorFactory">Factory for creating task executors based on task type.</param>
/// <param name="guidGenerator">Generator for creating unique identifiers.</param>
/// <param name="taskPersistenceStrategyFactory">Factory for task persistence strategies.</param>
/// <param name="taskFactory">Factory for creating task instances.</param>
/// <param name="workflowMetrics">Service for recording task execution metrics.</param>
/// <param name="instanceDataValidationService">Service for validating instance data against schema.</param>
/// <param name="logger">Logger for task execution telemetry.</param>
public sealed class LocalTaskExecutor(
    ITaskExecutorFactory taskExecutorFactory,
    IGuidGenerator guidGenerator,
    ITaskPersistenceStrategyFactory taskPersistenceStrategyFactory,
    ITaskFactory taskFactory,
    IWorkflowMetrics workflowMetrics,
    IInstanceDataValidationService instanceDataValidationService,
    ILogger<LocalTaskExecutor> logger) : ITaskOrchestrator
{
    /// <summary>
    /// Executes a task locally with comprehensive error handling and state management.
    /// This is the core implementation extracted from TaskExecutionService.ExecuteSingleTaskAsync.
    /// </summary>
    /// <param name="onExecuteTask">The task configuration to execute.</param>
    /// <param name="instanceTransitionId">The instance transition context. Can be null for extension tasks.</param>
    /// <param name="taskTrigger">The trigger type that initiated the task execution.</param>
    /// <param name="context">The script execution context for task execution.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method handles the complete lifecycle of a task execution including:
    /// - Task creation and initialization
    /// - Execution with error handling
    /// - Response processing and context updates
    /// - State transitions (Completed/Faulted)
    /// - Persistence handling via Strategy Pattern
    /// </remarks>
    public async Task ExecuteTaskAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        // Use TaskFactory for optimized task creation with proper isolation
        var task = await taskFactory.CreateExecutionTaskAsync(onExecuteTask.Task, cancellationToken);

        var taskExecutor = taskExecutorFactory.GetExecutor(task.GetTaskType());
        var instanceTask = new InstanceTask(
            guidGenerator.Create(),
            instanceTransitionId ?? guidGenerator.Create(),
            task.Key
        );

        // Get the appropriate persistence strategy based on TaskTrigger
        var persistenceStrategy = taskPersistenceStrategyFactory.GetStrategy(taskTrigger);

        // Handle task creation persistence
        await persistenceStrategy.HandleCreationAsync(instanceTask, cancellationToken);

        var taskType = task.GetTaskType().ToString();
        var sw = Stopwatch.StartNew();

        // Enrich all logs within this scope with comprehensive workflow context for distributed tracing
        using (logger.ForTask(
            domain: context.Workflow.Domain,
            flow: context.Workflow.Key,
            flowVersion: context.Workflow.Version,
            instanceId: context.Instance.Id,
            transitionKey: context.Transition?.Key,
            taskKey: task.Key,
            taskType: taskType,
            taskTrigger: taskTrigger.ToString()))
        {
            // Log task execution start
            logger.TaskExecutionStarted(
                TelemetryConstants.Prefixes.Execution,
                task.Key,
                taskType,
                context.Instance.Id);

            // Create span for task execution
            using var activity = WorkflowActivitySource.Instance.StartActivity(
                TelemetryConstants.SpanNames.TaskExecution,
                ActivityKind.Internal);

            activity?.SetTag(TelemetryConstants.TagNames.TaskKey, task.Key);
            activity?.SetTag(TelemetryConstants.TagNames.TaskType, taskType);
            activity?.SetTag(TelemetryConstants.TagNames.InstanceId, context.Instance.Id.ToString());
            activity?.SetDisplayName($"Task: {task.Key}");

            // Record task execution start 
            workflowMetrics.RecordTaskExecution(taskType, "started");

            try
            {
                // Determine script code to use based on mapping type
                string scriptCode;
                if (onExecuteTask.Mapping.Type.Equals(MappingType.Global) && task is IGlobalTask globalTask)
                {
                    // For global tasks, serialize the IMapping instance type name
                    if (globalTask.Mapping == null)
                    {
                        scriptCode = string.Empty;
                    }
                    else
                    {
                        // Serialize IMapping type name to Base64 string
                        var mappingTypeName = globalTask.Mapping.GetType().AssemblyQualifiedName ?? globalTask.Mapping.GetType().FullName ?? string.Empty;
                        var bytes = System.Text.Encoding.UTF8.GetBytes(mappingTypeName);
                        scriptCode = Convert.ToBase64String(bytes);
                    }
                }
                else
                {
                    // For local tasks, use the decoded script code from mapping
                    scriptCode = onExecuteTask.Mapping.DecodedCode;
                }

                var response = await taskExecutor.ExecuteAsync(
                    task,
                    scriptCode,
                    context,
                    cancellationToken);
                if (response != null)
                {
                    var variableKey = task.Key.ToVariableName();
                    context.TaskResponse[variableKey] = response;
                    if (taskTrigger != TaskTrigger.Extension && response is ScriptResponse scriptResponse)
                    {
                        if (scriptResponse.Data != null)
                        {
                            // NoTracking Instance
                            context.Instance.AddData(
                                guidGenerator.Create(),
                                new JsonData(JsonSerializer.Serialize(
                                    scriptResponse.Data, JsonSerializerConstants.JsonOptions)),
                                VersionStrategy.IncreasePatch
                            );
                              // Validate instance data after data change (OnDataChange mode)
                            var validationResult = await instanceDataValidationService.ValidateOnDataChangeAsync(
                                context.Instance,
                                context.Workflow,
                                logger,
                                cancellationToken);
                            
                            if (!validationResult.IsSuccess)
                            {
                                throw new InvalidOperationException(
                                    $"Instance data validation failed: {validationResult.Error.Message}");
                            }
                        }
                    }
                }

                instanceTask.Completed(
                    new JsonData(JsonSerializer.Serialize(response ?? new { }, JsonSerializerConstants.JsonOptions)));

                sw.Stop();

                // Log task execution completion
                logger.TaskExecutionCompleted(
                    TelemetryConstants.Prefixes.Execution,
                    task.Key,
                    taskType,
                    context.Instance.Id,
                    sw.ElapsedMilliseconds);

                // Record successful task completion 
                workflowMetrics.RecordTaskExecution(taskType, "success");
            }
            catch (Exception e)
            {
                sw.Stop();

                activity?.RecordExceptionWithStatus(e);

                instanceTask.Faulted(e.Message);

                // Log task execution failure
                logger.TaskExecutionFailed(
                    e,
                    TelemetryConstants.Prefixes.Execution,
                    task.Key,
                    taskType,
                    context.Instance.Id);

                // Record task failure
                workflowMetrics.RecordTaskExecution(taskType, "failure");

                throw;
            }

            // Handle task completion persistence
            await persistenceStrategy.HandleCompletionAsync(instanceTask, cancellationToken);
        }
    }

    /// <summary>
    /// Executes a task locally and returns context updates for synchronization.
    /// This method is specifically designed for distributed scenarios where context changes
    /// need to be synchronized back to the orchestration service.
    /// </summary>
    /// <param name="onExecuteTask">The task configuration to execute.</param>
    /// <param name="instanceTransitionId">The instance transition context. Can be null for extension tasks.</param>
    /// <param name="taskTrigger">The trigger type that initiated the task execution.</param>
    /// <param name="context">The script execution context for task execution.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    /// <returns>Context updates that occurred during task execution.</returns>
    public async Task<TaskContextUpdateOutput> ExecuteTaskWithContextUpdateAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        // Capture initial state for comparison
        var initialBody = context.Body;
        var initialInstanceDataCount = context.Instance.DataList.Count;

        // Execute the task (this already includes metrics recording)
        await ExecuteTaskAsync(onExecuteTask, instanceTransitionId, taskTrigger, context, cancellationToken);

        // Capture context changes
        var contextUpdate = new TaskContextUpdateOutput
        {
            TaskResponse = new Dictionary<string, object?>(context.TaskResponse)
        };

        // Capture instance data changes with proper thread safety
        if (context.Instance.DataList.Count > initialInstanceDataCount)
        {
            // Get the new data entries that were added during execution
            var newDataEntries = context.Instance.DataList
                .Skip(initialInstanceDataCount)
                .ToDictionary(
                    d => d.Id.ToString(), // Use version as key for proper synchronization
                    d => new TaskInstanceDataUpdatesOutput
                    {
                        Id = d.Id,
                        Data = d.Data.Json
                    });
            contextUpdate.InstanceDataUpdates = newDataEntries;
        }

        // Check if body was modified
        var bodyModified = !ReferenceEquals(initialBody, context.Body) ||
                           (initialBody != null && context.Body != null && !initialBody?.Equals(context.Body));

        if (bodyModified)
        {
            contextUpdate.Body = context.Body;
            contextUpdate.BodyModified = true;
        }

        // Copy metadata (if present)
        if (context.MetaData.Any())
        {
            contextUpdate.MetaData = new Dictionary<string, object>(context.MetaData);
        }

        return contextUpdate;
    }
}