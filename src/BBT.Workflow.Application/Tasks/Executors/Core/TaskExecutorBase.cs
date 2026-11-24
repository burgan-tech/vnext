using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Mapping;
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
/// <remarks>
/// Mapping (steps 2 and 6) is not executor code: an executor built with an <see cref="ITaskMappingRunner"/> gets the
/// default <see cref="PrepareInputAsync"/> / <see cref="ProcessOutputAsync"/>, which run the binding's script or
/// declarative mapping through that runner. An executor keeps only its type-specific behaviour, through the hooks
/// (<see cref="InputMappingFailurePrefix"/>, <see cref="FilesResponseWithoutOutputMapping"/>,
/// <see cref="UnmappedOutput"/>, ...) or by overriding a phase and calling <c>base</c>. An executor built without a
/// runner keeps the no-mapping defaults (input null, output <c>Data</c>).
/// </remarks>
/// <typeparam name="TTask">The specific WorkflowTask type this executor handles.</typeparam>
public abstract class TaskExecutorBase<TTask> : ITaskExecutor
    where TTask : WorkflowTask
{
    protected readonly ILogger Logger;

    private TaskMappingSite? _mappingSite;

    /// <summary>An executor without a mapping runner: its phases map nothing unless it overrides them.</summary>
    protected TaskExecutorBase(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>An executor whose mapping phases run through <paramref name="mappingRunner"/>.</summary>
    protected TaskExecutorBase(ILogger logger, ITaskMappingRunner mappingRunner)
        : this(logger)
    {
        MappingRunner = mappingRunner;
    }

    /// <summary>The runner of this executor's binding mappings; null for an executor that maps nothing.</summary>
    protected ITaskMappingRunner? MappingRunner { get; }

    /// <summary>
    /// Prefix of a failed script input mapping's message, kept from the executor's former mapping block. Default
    /// <c>"{TaskType} task input handler failed: "</c>.
    /// </summary>
    protected virtual string InputMappingFailurePrefix => $"{TaskType} task input handler failed: ";

    /// <summary>Prefix of a failed script output mapping's message. Default <c>"{TaskType} task output handler failed: "</c>.</summary>
    protected virtual string OutputMappingFailurePrefix => $"{TaskType} task output handler failed: ";

    /// <summary>Whether a failed script input mapping raises <c>TaskInputHandlerFailed</c> on <see cref="Logger"/>.</summary>
    protected virtual bool LogsScriptInputMappingFailures => true;

    /// <summary>Whether a failed script output mapping raises <c>TaskOutputHandlerFailed</c> on <see cref="Logger"/>.</summary>
    protected virtual bool LogsScriptOutputMappingFailures => true;

    /// <summary>Whether the script mapping compiles through the per-execution memo (<see cref="GetOrCompileMappingAsync{T}"/>).</summary>
    protected virtual bool MemoizesScriptMappingCompile => true;

    /// <summary>
    /// Whether the task response is filed on the script context (<see cref="UpdateScriptContextWithResponse"/>) even
    /// when the binding maps no output. A mapped output always files it first. Default false.
    /// </summary>
    protected virtual bool FilesResponseWithoutOutputMapping => false;

    /// <summary>The output <c>Data</c> when the binding maps no output. Default <see cref="TaskInvocationResult.Data"/>.</summary>
    protected virtual object? UnmappedOutput(TaskInvocationResult invocationResult) => invocationResult.Data;

    /// <summary>This executor's labels, logger and flags for the mapping runner.</summary>
    protected TaskMappingSite MappingSite => _mappingSite ??= new TaskMappingSite(
        Logger, TaskType.ToString(), InputMappingFailurePrefix, OutputMappingFailurePrefix)
    {
        LogsScriptInputFailures = LogsScriptInputMappingFailures,
        LogsScriptOutputFailures = LogsScriptOutputMappingFailures,
        MemoizesScriptCompile = MemoizesScriptMappingCompile
    };

    /// <inheritdoc />
    public abstract TaskType TaskType { get; }

    /// <inheritdoc />
    public async Task<Result<StandardTaskResponse>> ExecuteAsync(
        TaskExecutorContext context,
        CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var taskKey = context.Task.Key;

        Logger.TaskExecutorStarting(taskKey, GetType().Name);

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
        using (var phaseActivity = TaskExecutionActivityHelper.StartActivity(TaskExecutionActivityHelper.OperationPrepareInput, taskKey, taskTypeStr))
        {
            inputResult = await PrepareInputAsync(task, context, cancellationToken);
            if (!inputResult.IsSuccess)
                phaseActivity.SetResultError(inputResult.Error.Code, inputResult.Error.Message);
        }
        if (!inputResult.IsSuccess)
        {
            Logger.LogError("Task {TaskKey} input preparation failed: {Error}",
                taskKey, inputResult.Error.Message);
            return Result<StandardTaskResponse>.Fail(inputResult.Error);
            // return CreateErrorResponse(inputResult.Error, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
        
        context.InputResponse = inputResult.Value;

        // 3. PreProcess (virtual - optional)
        var preProcessResult = await PreProcessAsync(task, context, cancellationToken);
        if (!preProcessResult.IsSuccess)
        {
            Logger.LogError("Task {TaskKey} pre-processing failed: {Error}",
                taskKey, preProcessResult.Error.Message);
            return Result<StandardTaskResponse>.Fail(preProcessResult.Error);
            // return CreateErrorResponse(preProcessResult.Error, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }

        // 4. Invoke (abstract or virtual)
        Result<TaskInvocationResult> invokeResult;
        using (var phaseActivity = TaskExecutionActivityHelper.StartActivity(TaskExecutionActivityHelper.OperationInvoke, taskKey, taskTypeStr))
        {
            invokeResult = await InvokeAsync(task, context, cancellationToken);
            if (!invokeResult.IsSuccess)
                phaseActivity.SetResultError(invokeResult.Error.Code, invokeResult.Error.Message);
        }
        if (!invokeResult.IsSuccess)
        {
            Logger.LogError("Task {TaskKey} invocation failed: {Error}",
                taskKey, invokeResult.Error.Message);
            return CreateErrorResponse(invokeResult.Error, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }

        // Materialize the audit payload once while the invocation result is already hot. Carrying
        // the full object graph until the journal write extends its lifetime without reducing the
        // number of serializations; JsonData keeps only the persisted text and parses lazily if a
        // later consumer actually requests JsonElement.
        // Skipped when no journal row is persisted (no persistence strategy, e.g. history: none):
        // the payload would be serialized only to be dropped.
        if (context.CapturesJournalPayloads)
        {
            context.RawInvocationResult = invokeResult.Value is null
                ? new JsonData("null")
                : new JsonData(invokeResult.Value);
        }

        // Note: Business errors (HTTP 4xx/5xx) are NOT intercepted here.
        // The invocation result (including IsSuccess=false, StatusCode, Metadata/ExceptionType)
        // is passed through to CreateSuccessResponse and handled by TaskCoordinator
        // for Error Boundary policy resolution.

        // 5. PostProcess (virtual - optional, e.g., correlation)
        var postProcessResult = await PostProcessAsync(task, invokeResult.Value!, context, cancellationToken);
        if (!postProcessResult.IsSuccess)
        {
            Logger.LogError("Task {TaskKey} post-processing failed: {Error}",
                taskKey, postProcessResult.Error.Message);
            return Result<StandardTaskResponse>.Fail(postProcessResult.Error);
            // return CreateErrorResponse(postProcessResult.Error, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }

        // 6. ProcessOutput (virtual - custom per executor)
        Result<object?> outputResult;
        using (var phaseActivity = TaskExecutionActivityHelper.StartActivity(TaskExecutionActivityHelper.OperationProcessOutput, taskKey, taskTypeStr))
        {
            outputResult = await ProcessOutputAsync(task, invokeResult.Value!, context, cancellationToken);
            if (!outputResult.IsSuccess)
                phaseActivity.SetResultError(outputResult.Error.Code, outputResult.Error.Message);
        }
        if (!outputResult.IsSuccess)
        {
            Logger.LogError("Task {TaskKey} output processing failed: {Error}",
                taskKey, outputResult.Error.Message);
            return Result<StandardTaskResponse>.Fail(outputResult.Error);
            // return CreateErrorResponse(outputResult.Error, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
        
        if (context.TaskTrigger == TaskTrigger.Extension)
        {
            // The Extension-trigger gate is unchanged (pre-existing); only the KEY inside it can
            // move, and only when the caller opted in via ResponseVariableKey — never inferred from
            // the trigger itself, since custom Functions also run with TaskTrigger.Extension and
            // must keep reading their output by task key (see TaskEngineExecutionOptions).
            context.ScriptContext.SetOutputResponse(outputResult.Value, context.ResponseVariableKey ?? taskKey.ToVariableName());
        }


        // 7. CreateResponse
        return CreateSuccessResponse(task, invokeResult.Value!, outputResult.Value, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
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
    /// Runs the binding's input mapping (script or declarative) through <see cref="MappingRunner"/>; null when the
    /// binding maps no input or the executor has no runner. Override to add type-specific input behaviour.
    /// </summary>
    protected virtual Task<Result<ScriptResponse?>> PrepareInputAsync(
        TTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        return MappingRunner is null
            ? Task.FromResult(Result<ScriptResponse?>.Ok(null))
            : MappingRunner.PrepareInputAsync(task, context, cancellationToken, MappingSite);
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
    /// Runs the binding's output mapping (script or declarative) through <see cref="MappingRunner"/> and returns the
    /// output <c>Data</c>. A mapped output first files the task response on the script context; an unmapped one files
    /// it only when <see cref="FilesResponseWithoutOutputMapping"/> and returns <see cref="UnmappedOutput"/>. Without a
    /// runner the result's <c>Data</c> passes through. Override to add type-specific output behaviour.
    /// </summary>
    protected virtual Task<Result<object?>> ProcessOutputAsync(
        TTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        if (MappingRunner is null)
            return Task.FromResult(Result<object?>.Ok(invocationResult.Data));

        if (TaskMappingRouter.OutputKind(context.OnExecuteTask) == TaskMappingKind.PassThrough)
        {
            if (FilesResponseWithoutOutputMapping)
                UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext, context.ResponseVariableKey);
            return Task.FromResult(Result<object?>.Ok(UnmappedOutput(invocationResult)));
        }

        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext, context.ResponseVariableKey);
        return MappingRunner.ProcessOutputAsync(task, invocationResult, context, cancellationToken, MappingSite);
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
    /// Compiles the task's mapping once per task execution and hands out a FRESH instance per call (the memo lives on
    /// the <see cref="TaskExecutorContext"/>). Moved to <see cref="ScriptTaskMappingRunner.GetOrCompileMappingAsync{T}"/>;
    /// kept here for executors that compile the binding's mapping outside the mapping phases (the Script task's invoke).
    /// </summary>
    protected static Task<T> GetOrCompileMappingAsync<T>(
        IScriptEngine scriptEngine,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
        where T : class =>
        ScriptTaskMappingRunner.GetOrCompileMappingAsync<T>(scriptEngine, context, cancellationToken);

    /// <summary>
    /// Updates script context with response data for output handler processing.
    /// Sets the standard response on the context and optionally sets output response for extension triggers.
    /// </summary>
    /// <param name="taskKey">The task key used to generate the variable name.</param>
    /// <param name="result">The task invocation result (can be null).</param>
    /// <param name="context">The script context to update.</param>
    /// <param name="responseVariableKey">
    /// Caller-supplied override for the variable name (from
    /// <c>TaskExecutorContext.ResponseVariableKey</c>). Null means "derive from
    /// <paramref name="taskKey"/>" — today's behavior. This is the site the Preprod crash traced
    /// back to (the <c>TaskResponse</c> merge): two extensions sharing a task key overwrote each
    /// other's entry here, so this parameter must be honored on every call site, not just the
    /// Extension-only <c>OutputResponse</c> write in <see cref="ExecuteAsync"/>.
    /// </param>
    protected static void UpdateScriptContextWithResponse(
        string taskKey,
        TaskInvocationResult? result,
        ScriptContext context,
        string? responseVariableKey = null)
    {
        var variableKey = responseVariableKey ?? taskKey.ToVariableName();
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

    /// <summary>
    /// The envelope with the caller's credential (<c>sub</c>, <c>act_sub</c>, <c>position</c>, <c>client_id</c>,
    /// <c>role</c>) added to its binding's <c>Headers</c> wherever the task's own mapping did not set them
    /// (<see cref="Execution.HttpTaskInvocation.WithCallerCredential"/>). Done here, on the orchestrator side, so the same
    /// header set travels whether the task runs in-process or in the Execution host — the invoke envelope's trace context
    /// carries only <c>sub</c> / <c>act_sub</c>. The caller values are its request headers, so a role a provider resolved
    /// (morph-idm) is never carried.
    /// </summary>
    protected static TaskEnvelope WithCallerCredential(TaskEnvelope envelope, ScriptContext? scriptContext)
    {
        if (envelope.Binding.ValueKind != JsonValueKind.Object)
            return envelope;

        // Read Headers in place; the binding is only parsed into a mutable node (and re-serialized) when a
        // credential actually has to be added — the common "mapping already set it / no caller credential"
        // case returns the envelope untouched without touching the rest of the binding.
        var current = envelope.Binding.TryGetProperty("Headers", out var headersElement)
            ? headersElement.ValueKind switch
            {
                JsonValueKind.String => headersElement.GetString(),
                JsonValueKind.Null => null,
                _ => throw new InvalidOperationException(
                    $"Task binding 'Headers' must be a JSON string, was {headersElement.ValueKind}.")
            }
            : null;
        var merged = Execution.HttpTaskInvocation.WithCallerCredential(current, CallerHeadersOf(scriptContext));
        if (merged == current)
            return envelope;

        var binding = System.Text.Json.Nodes.JsonNode.Parse(envelope.Binding.GetRawText())!.AsObject();
        binding["Headers"] = merged;
        return new TaskEnvelope
        {
            TaskType = envelope.TaskType,
            Version = envelope.Version,
            TaskKey = envelope.TaskKey,
            Binding = JsonSerializer.SerializeToElement(binding)
        };
    }

    /// <summary>The caller's request headers, the source of the forwarded credential (never provider-resolved roles).</summary>
    protected static IReadOnlyDictionary<string, string>? CallerHeadersOf(ScriptContext? scriptContext) =>
        scriptContext?.Headers is null ? null : scriptContext.GetHeadersAsDictionary();
}
