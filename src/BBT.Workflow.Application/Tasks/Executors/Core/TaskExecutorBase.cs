using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
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
public abstract class TaskExecutorBase<TTask>(ILogger logger, IWorkflowMetrics metrics) : ITaskExecutor
    where TTask : WorkflowTask
{
    protected readonly ILogger Logger = logger;
    protected readonly IWorkflowMetrics Metrics = metrics;

    private const string ScriptLanguage = "csharp";

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
        var taskTypeStr = context.Task.GetTaskType().ToString();

        // 2. PrepareInput (virtual - custom per executor)
        Result<ScriptResponse?> inputResult;
        var hasMapping = context.OnExecuteTask?.Mapping?.HasMappingCode == true;
        using (TaskExecutionActivityHelper.StartActivity(TaskExecutionActivityHelper.OperationPrepareInput, taskKey, taskTypeStr))
        {
            var phaseStart = Stopwatch.GetTimestamp();
            try
            {
                inputResult = await PrepareInputAsync(task, context, cancellationToken);
            }
            catch (Exception ex) when (hasMapping && ex is not OperationCanceledException)
            {
                Metrics.RecordScriptRuntimeError("task-input", ScriptLanguage, ex.GetType().Name);
                throw;
            }
            if (hasMapping)
            {
                Metrics.RecordScriptExecutionDuration(
                    "task-input", ScriptLanguage,
                    inputResult.IsSuccess ? "success" : "failure",
                    Stopwatch.GetElapsedTime(phaseStart).TotalSeconds);
            }
        }
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
        Result<TaskInvocationResult> invokeResult;
        using (TaskExecutionActivityHelper.StartActivity(TaskExecutionActivityHelper.OperationInvoke, taskKey, taskTypeStr))
        {
            invokeResult = await InvokeAsync(task, context, cancellationToken);
        }
        if (!invokeResult.IsSuccess)
        {
            stopwatch.Stop();
            Logger.LogError("Task {TaskKey} invocation failed: {Error}",
                taskKey, invokeResult.Error.Message);
            return CreateErrorResponse(invokeResult.Error, stopwatch.ElapsedMilliseconds);
        }

        context.RawInvocationResultJson = JsonSerializer.Serialize(invokeResult.Value!, JsonSerializerConstants.JsonOptions);

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
        Result<object?> outputResult;
        using (TaskExecutionActivityHelper.StartActivity(TaskExecutionActivityHelper.OperationProcessOutput, taskKey, taskTypeStr))
        {
            var phaseStart = Stopwatch.GetTimestamp();
            try
            {
                outputResult = await ProcessOutputAsync(task, invokeResult.Value!, context, cancellationToken);
            }
            catch (Exception ex) when (hasMapping && ex is not OperationCanceledException)
            {
                Metrics.RecordScriptRuntimeError("task-output", ScriptLanguage, ex.GetType().Name);
                throw;
            }
            if (hasMapping)
            {
                Metrics.RecordScriptExecutionDuration(
                    "task-output", ScriptLanguage,
                    outputResult.IsSuccess ? "success" : "failure",
                    Stopwatch.GetElapsedTime(phaseStart).TotalSeconds);
            }
        }
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
        return CreateSuccessResponse(task, invokeResult.Value!, outputResult.Value, stopwatch.ElapsedMilliseconds);
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
    /// When the task defines <c>AcceptedStatusCodes</c> and the response status code matches,
    /// <c>IsSuccess</c> is overridden to <c>true</c> regardless of the HTTP error status.
    /// </summary>
    protected virtual Result<StandardTaskResponse> CreateSuccessResponse(
        WorkflowTask task,
        TaskInvocationResult invocationResult,
        object? outputData,
        long executionDurationMs)
    {
        var acceptedCodes = GetAcceptedStatusCodes(task);
        var effectiveIsSuccess = invocationResult.IsSuccess
            || acceptedCodes.IsAcceptedStatusCode(invocationResult.StatusCode);

        return Result<StandardTaskResponse>.Ok(new StandardTaskResponse
        {
            IsSuccess = effectiveIsSuccess,
            Data = outputData,
            StatusCode = invocationResult.StatusCode,
            Headers = invocationResult.Headers,
            Metadata = invocationResult.Metadata,
            ExecutionDurationMs = executionDurationMs,
            TaskType = TaskType.ToString(),
            ErrorMessage = effectiveIsSuccess ? null : invocationResult.ErrorMessage
        });
    }

    /// <summary>
    /// Extracts the <c>AcceptedStatusCodes</c> list from the task if it supports it.
    /// </summary>
    private static IReadOnlyList<string>? GetAcceptedStatusCodes(WorkflowTask task) => task switch
    {
        HttpTask http => http.AcceptedStatusCodes,
        DaprServiceTask daprService => daprService.AcceptedStatusCodes,
        DirectTriggerTask directTrigger => directTrigger.AcceptedStatusCodes,
        GetInstancesTask getInstances => getInstances.AcceptedStatusCodes,
        GetInstanceDataTask getInstanceData => getInstanceData.AcceptedStatusCodes,
        GetInstanceTask getInstance => getInstance.AcceptedStatusCodes,
        StartTask startTask => startTask.AcceptedStatusCodes,
        SubProcessTask subProcess => subProcess.AcceptedStatusCodes,
        _ => null
    };

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
    /// Compiles the task's mapping once per task execution and hands out a FRESH instance per call.
    /// The compiled factory is memoized on the <see cref="TaskExecutorContext"/> (keyed by mapping +
    /// target type), so PrepareInput and ProcessOutput/Invoke asking for the same mapping share a
    /// single engine call instead of each paying their own compile-cache lookup; instance-per-phase
    /// semantics are unchanged — a user script holding instance fields observes exactly today's
    /// behaviour, one fresh instance per phase.
    /// </summary>
    /// <remarks>
    /// No lock guards the memo dictionary: a single task execution runs its phases sequentially on
    /// one <see cref="TaskExecutorContext"/> within one thread (the pipeline does not fan phases of
    /// the SAME task out concurrently), so there is no concurrent writer to race against.
    /// </remarks>
    protected static async Task<T> GetOrCompileMappingAsync<T>(
        IScriptEngine scriptEngine,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        var mapping = context.OnExecuteTask.Mapping;
        var key = (mapping, typeof(T));
        context.CompiledMappingFactories ??= new Dictionary<(ScriptCode, Type), object>();
        if (!context.CompiledMappingFactories.TryGetValue(key, out var boxed))
        {
            boxed = await scriptEngine.CompileToFactoryAsync<T>(
                mapping, context.ScriptContext.Workflow?.Scripts, cancellationToken);
            context.CompiledMappingFactories[key] = boxed;
        }

        return ((Func<T>)boxed)();
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
            Body = result?.Body,
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

