using System.Diagnostics;
using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Base class for task executors using the Template Method Pattern.
/// Provides a consistent lifecycle for task execution:
/// 1. Validate - Validate task context and requirements
/// 2. PrepareInput - Custom input mapping (virtual)
/// 3. PreProcess - Pre-processing logic (virtual)
/// 4. Invoke - Task invocation (abstract)
/// 5. PostProcess - Post-processing like correlation (virtual)
/// 6. ProcessOutput - Custom output mapping (virtual)
/// 7. CreateResponse - Build StandardTaskResponse
/// </summary>
/// <typeparam name="TTask">The specific WorkflowTask type this executor handles.</typeparam>
public abstract class TaskExecutorBase<TTask>(ILogger logger) : ITaskExecutor
    where TTask : WorkflowTask
{
    protected readonly ILogger Logger = logger;

    /// <inheritdoc />
    public abstract TaskType TaskType { get; }

    /// <inheritdoc />
    public async Task<Result<StandardTaskResponse>> ExecuteAsync(
        TaskExecutorContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var taskKey = context.Task.Key;

        Logger.LogDebug("Executing task {TaskKey} with executor {Executor}",
            taskKey, GetType().Name);

        // 1. Validate & Cast
        var validationResult = ValidateContext(context);
        if (!validationResult.IsSuccess)
        {
            return Result<StandardTaskResponse>.Fail(validationResult.Error);
        }

        var task = (TTask)context.Task;

        // 2. PrepareInput (virtual - custom per executor)
        var inputResult = await PrepareInputAsync(task, context, cancellationToken);
        if (!inputResult.IsSuccess)
        {
            stopwatch.Stop();
            Logger.LogError("Task {TaskKey} input preparation failed: {Error}",
                taskKey, inputResult.Error.Message);
            return Result<StandardTaskResponse>.Fail(inputResult.Error);
            // return CreateErrorResponse(inputResult.Error, stopwatch.ElapsedMilliseconds);
        }
        
        context.InputResponse = inputResult.Value;

        // 3. PreProcess (virtual - optional)
        var preProcessResult = await PreProcessAsync(task, context, cancellationToken);
        if (!preProcessResult.IsSuccess)
        {
            stopwatch.Stop();
            Logger.LogError("Task {TaskKey} pre-processing failed: {Error}",
                taskKey, preProcessResult.Error.Message);
            return Result<StandardTaskResponse>.Fail(preProcessResult.Error);
            // return CreateErrorResponse(preProcessResult.Error, stopwatch.ElapsedMilliseconds);
        }

        // 4. Invoke (abstract or virtual)
        var invokeResult = await InvokeAsync(task, context, cancellationToken);
        if (!invokeResult.IsSuccess)
        {
            stopwatch.Stop();
            Logger.LogError("Task {TaskKey} invocation failed: {Error}",
                taskKey, invokeResult.Error.Message);
            return CreateErrorResponse(invokeResult.Error, stopwatch.ElapsedMilliseconds);
        }

        // Note: Business errors (HTTP 4xx/5xx) are NOT intercepted here.
        // The invocation result (including IsSuccess=false, StatusCode, Metadata/ExceptionType)
        // is passed through to CreateSuccessResponse and handled by TaskCoordinator
        // for Error Boundary policy resolution.

        // 5. PostProcess (virtual - optional, e.g., correlation)
        var postProcessResult = await PostProcessAsync(task, invokeResult.Value!, context, cancellationToken);
        if (!postProcessResult.IsSuccess)
        {
            stopwatch.Stop();
            Logger.LogError("Task {TaskKey} post-processing failed: {Error}",
                taskKey, postProcessResult.Error.Message);
            return Result<StandardTaskResponse>.Fail(postProcessResult.Error);
            // return CreateErrorResponse(postProcessResult.Error, stopwatch.ElapsedMilliseconds);
        }

        // 6. ProcessOutput (virtual - custom per executor)
        var outputResult = await ProcessOutputAsync(task, invokeResult.Value!, context, cancellationToken);
        if (!outputResult.IsSuccess)
        {
            stopwatch.Stop();
            Logger.LogError("Task {TaskKey} output processing failed: {Error}",
                taskKey, outputResult.Error.Message);
            return Result<StandardTaskResponse>.Fail(outputResult.Error);
            // return CreateErrorResponse(outputResult.Error, stopwatch.ElapsedMilliseconds);
        }
        
        if (context.TaskTrigger == TaskTrigger.Extension)
        {
            context.ScriptContext.SetOutputResponse(outputResult.Value, taskKey.ToVariableName());
        }

        stopwatch.Stop();

        // 7. CreateResponse
        return CreateSuccessResponse(invokeResult.Value!, outputResult.Value, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Validates the execution context and task type.
    /// </summary>
    protected virtual Result ValidateContext(TaskExecutorContext context)
    {
        if (context.Task.GetTaskType() != TaskType)
        {
            return Result.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                $"Task {context.Task.Key} is not of type {TaskType}"));
        }

        if (context.Task is not TTask)
        {
            return Result.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                $"Task {context.Task.Key} cannot be cast to {typeof(TTask).Name}"));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Prepares input data and runs input mapping scripts.
    /// Override to implement custom input logic.
    /// </summary>
    protected virtual Task<Result<ScriptResponse?>> PrepareInputAsync(
        TTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Result<ScriptResponse?>.Ok(null));
    }

    /// <summary>
    /// Performs pre-processing before task invocation.
    /// Override to implement custom pre-processing logic.
    /// </summary>
    protected virtual Task<Result> PreProcessAsync(
        TTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Result.Ok());
    }

    /// <summary>
    /// Invokes the task execution.
    /// Must be implemented by concrete executors.
    /// </summary>
    protected abstract Task<Result<TaskInvocationResult>> InvokeAsync(
        TTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Performs post-processing after task invocation.
    /// Override to implement custom post-processing logic (e.g., correlation saving).
    /// </summary>
    protected virtual Task<Result> PostProcessAsync(
        TTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Result.Ok());
    }

    /// <summary>
    /// Processes output data and runs output mapping scripts.
    /// Override to implement custom output logic.
    /// Returns optional transformed data.
    /// </summary>
    protected virtual Task<Result<object?>> ProcessOutputAsync(
        TTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Result<object?>.Ok(invocationResult.Data));
    }

    /// <summary>
    /// Creates a success response from invocation result.
    /// </summary>
    protected virtual Result<StandardTaskResponse> CreateSuccessResponse(
        TaskInvocationResult invocationResult,
        object? outputData,
        long executionDurationMs)
    {
        return Result<StandardTaskResponse>.Ok(new StandardTaskResponse
        {
            IsSuccess = invocationResult.IsSuccess,
            Data = outputData,
            StatusCode = invocationResult.StatusCode,
            Headers = invocationResult.Headers,
            Metadata = invocationResult.Metadata,
            ExecutionDurationMs = executionDurationMs,
            TaskType = TaskType.ToString(),
            ErrorMessage = invocationResult.ErrorMessage
        });
    }

    /// <summary>
    /// Creates an error response.
    /// </summary>
    protected virtual Result<StandardTaskResponse> CreateErrorResponse(
        Error error,
        long executionDurationMs)
    {
        return Result<StandardTaskResponse>.Ok(new StandardTaskResponse
        {
            IsSuccess = false,
            ErrorMessage = error.Message,
            StatusCode = 500,
            ExecutionDurationMs = executionDurationMs,
            TaskType = TaskType.ToString()
        });
    }

    /// <summary>
    /// Updates script context with response data for output handler processing.
    /// Sets the standard response on the context and optionally sets output response for extension triggers.
    /// </summary>
    /// <param name="taskKey">The task key used to generate the variable name.</param>
    /// <param name="result">The task invocation result (can be null).</param>
    /// <param name="context">The script context to update.</param>
    protected static void UpdateScriptContextWithResponse(
        string taskKey,
        TaskInvocationResult? result,
        ScriptContext context)
    {
        var variableKey = taskKey.ToVariableName();
        var response = new StandardTaskResponse
        {
            IsSuccess = result?.IsSuccess == true,
            Data = result?.Data,
            StatusCode = result?.StatusCode,
            Headers = result?.Headers,
            ErrorMessage = result?.ErrorMessage,
            ExecutionDurationMs = result?.ExecutionDurationMs,
            TaskType = result?.TaskType,
            Metadata = result?.Metadata
        };

        context.SetStandardResponse(response, variableKey);
    }
}

