using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Agents;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Runs an MCP task (type 24, issue #1105) in-process on the orchestration host: there is no Execution
/// hop. Input and output follow the standard task mapping (<c>IMapping</c>): the input handler sets
/// <see cref="McpTask.SetArguments"/>, the output handler reads the normalized content from the response.
/// The endpoint host is checked against <c>Mcp:AllowedHosts</c> on every call and secrets are resolved
/// at call time, never read from the component.
/// </summary>
public sealed class McpTaskExecutor(
    IMcpClientGateway gateway,
    ISecretResolver secretResolver,
    IScriptEngine scriptEngine,
    IOptions<McpOptions> options,
    ILogger<McpTaskExecutor> logger)
    : TaskExecutorBase<McpTask>(logger)
{
    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Mcp;

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        McpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
            return Result<ScriptResponse?>.Ok(null);

        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            var runner = await GetOrCompileMappingAsync<IMapping>(scriptEngine, context, ct);
            return await runner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"MCP task input handler failed: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(task.Key, TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty, result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        McpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var call = await CallAsync(task, cancellationToken);
        if (!call.IsSuccess)
        {
            Logger.McpCallFailed(task.Operation, task.Key, call.Error ?? "unknown");
        }

        return Result<TaskInvocationResult>.Ok(ToInvocationResult(call.Result, call.Error, call.StatusCode, call.ElapsedMs));
    }

    /// <summary>
    /// Runs the task's operation and returns the gateway result; shared with the agent's MCP tools.
    /// </summary>
    public async Task<(bool IsSuccess, McpCallResult Result, string? Error, int StatusCode, long ElapsedMs)> CallAsync(
        McpTask task,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        if (!string.Equals(task.Transport, McpTransports.Http, StringComparison.OrdinalIgnoreCase))
            return Fail("MCP transport must be 'http'.", 400);

        if (!McpOptions.IsHostAllowed(task.Endpoint, options.Value.AllowedHosts, out var host))
            return Fail($"MCP endpoint host '{host ?? task.Endpoint}' is not in Mcp:AllowedHosts.", 403);

        if (task.Operation == McpOperations.CallTool && string.IsNullOrWhiteSpace(task.Tool))
            return Fail("MCP callTool requires 'tool'.", 400);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (task.Auth is { } auth)
        {
            var secret = await secretResolver.ResolveAsync(auth.SecretRef, cancellationToken);
            if (string.IsNullOrEmpty(secret))
                return Fail($"MCP auth secret '{auth.SecretRef}' could not be resolved.", 500);
            headers[auth.Header] = string.IsNullOrWhiteSpace(auth.Scheme) ? secret : $"{auth.Scheme} {secret}";
        }

        foreach (var (name, secretRef) in task.Headers)
        {
            var value = await secretResolver.ResolveAsync(secretRef, cancellationToken);
            if (string.IsNullOrEmpty(value))
                return Fail($"MCP header secret '{secretRef}' could not be resolved.", 500);
            headers[name] = value;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(task.TimeoutSeconds, 1, McpTask.MaxTimeoutSeconds));
        var request = new McpRequest(task.Endpoint, headers, task.Operation, task.Tool, task.Uri, task.Prompt, task.Arguments, timeout);

        McpCallResult result;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            result = await gateway.ExecuteAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail($"MCP {task.Operation} did not answer within {timeout.TotalSeconds:0}s.", 408);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"MCP {task.Operation} failed: {ex.Message}", 502);
        }

        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!result.IsSuccess)
            return (false, result, result.Error, 502, elapsed);
        if (result.IsToolError)
            return (false, result, result.Error ?? "MCP tool reported an error.", 422, elapsed);

        return (true, result, null, 200, elapsed);

        (bool, McpCallResult, string?, int, long) Fail(string message, int status) =>
            (false, McpCallResult.Failure(message), message, status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <inheritdoc />
    protected override async Task<Result<object?>> ProcessOutputAsync(
        McpTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
            return Result<object?>.Ok(invocationResult.Data);

        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext, context.ResponseVariableKey);

        return await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var runner = await GetOrCompileMappingAsync<IMapping>(scriptEngine, context, ct);
            var output = await runner.OutputHandler(context.ScriptContext);
            return output.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"MCP task output handler failed: {ScriptDiagnostics.Explain(ex)}"));
    }

    private static TaskInvocationResult ToInvocationResult(McpCallResult call, string? error, int statusCode, long elapsedMs)
    {
        object? data = null;
        if (call.Content is { } content)
            data = JsonSerializer.Deserialize<object>(content.GetRawText(), JsonSerializerConstants.JsonOptions);
        else if (call.Tools is { } tools)
            data = tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }).ToList();

        return new TaskInvocationResult
        {
            IsSuccess = error is null,
            StatusCode = statusCode,
            Data = data,
            ErrorMessage = error,
            TaskType = "mcp",
            ExecutionDurationMs = elapsedMs
        };
    }
}
