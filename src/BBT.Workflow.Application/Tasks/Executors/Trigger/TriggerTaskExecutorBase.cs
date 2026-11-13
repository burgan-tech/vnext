using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Base class for trigger task executors that need domain-aware routing.
/// Provides common functionality for determining local vs remote execution.
/// </summary>
/// <typeparam name="TTask">The specific trigger task type.</typeparam>
public abstract class TriggerTaskExecutorBase<TTask>(
    IScriptEngine scriptEngine,
    IRuntimeInfoProvider runtimeInfoProvider,
    IRemoteInvokerService remoteInvoker,
    ILogger logger)
    : TaskExecutorBase<TTask>(logger)
    where TTask : WorkflowTask
{
    protected readonly IScriptEngine ScriptEngine = scriptEngine;
    protected readonly IRuntimeInfoProvider RuntimeInfoProvider = runtimeInfoProvider;
    protected readonly IRemoteInvokerService RemoteInvoker = remoteInvoker;

    /// <summary>
    /// Gets the target domain for the task.
    /// </summary>
    protected abstract string GetTargetDomain(TTask task);

    /// <summary>
    /// Checks if the target domain is served by this process (the current domain or another domain
    /// hosted beside it), so the task runs in-process instead of through Dapr.
    /// </summary>
    protected bool IsSameDomain(TTask task)
    {
        var targetDomain = GetTargetDomain(task);
        if (string.IsNullOrEmpty(targetDomain))
            return true;

        return string.Equals(RuntimeInfoProvider.Domain, targetDomain, StringComparison.OrdinalIgnoreCase)
               || RuntimeInfoProvider.IsDomainMatch(targetDomain);
    }

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        TTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<ScriptResponse?>.Ok(null);
        }

        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            var scriptRunner = await GetOrCompileMappingAsync<IMapping>(ScriptEngine, context, ct);

            return await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Input handler failed for {TaskType}: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <inheritdoc />
    protected override async Task<Result<object?>> ProcessOutputAsync(
        TTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Update script context with response
        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext, context.ResponseVariableKey);

        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await GetOrCompileMappingAsync<IMapping>(ScriptEngine, context, ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Output handler failed for {TaskType}: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskOutputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <summary>
    /// Runs a LOCAL (same-domain, in-process) invocation inside its own span AND its own trace
    /// lane, so the invocation is visible under <c>Task.Execute.*</c> and everything the
    /// invocation enqueues stays there too.
    /// <para>
    /// Two problems this solves, both observed in production traces:
    /// (1) the local branch produced no span at all — unlike the remote branch, whose Dapr/HTTP
    /// client span makes the request visible — so the target and cost of the invocation were
    /// unreadable; (2) transition jobs accepted by the invocation stamped
    /// <see cref="WorkflowTraceLane.Current"/> as their lane anchor, which at that moment was the
    /// <em>executing instance's</em> lane, so the triggered work surfaced as siblings of the
    /// current instance's hops instead of under the task that caused it.
    /// </para>
    /// <para>
    /// The fix mirrors the subflow handoff: <see cref="WorkflowTraceLane.EnterChildLane"/> makes
    /// the just-started <c>Trigger.Local.*</c> span the lane anchor for the triggered instance's
    /// hops (flat underneath it, exactly like a subflow's lane under its forward span). For
    /// read-only tasks (GetInstance/GetInstances/GetInstanceData) the child lane is a harmless
    /// no-op — they enqueue nothing — kept uniform so every trigger-family local call behaves
    /// identically.
    /// </para>
    /// </summary>
    /// <param name="task">The task being executed (supplies key/type/target tags).</param>
    /// <param name="targetFlow">Target workflow, when known.</param>
    /// <param name="targetInstance">Target instance identifier, when known.</param>
    /// <param name="action">The local invocation body.</param>
    /// <param name="cancellationToken">Cancellation token, forwarded to the body.</param>
    protected async Task<Result<TaskInvocationResult>> RunLocalScopedAsync(
        TTask task,
        string? targetFlow,
        string? targetInstance,
        Func<CancellationToken, Task<Result<TaskInvocationResult>>> action,
        CancellationToken cancellationToken)
    {
        using var activity = TaskExecutionActivityHelper.StartLocalTriggerActivity(
            task.Key,
            TaskType.ToString(),
            GetTargetDomain(task),
            targetFlow,
            targetInstance);

        // Anchor AFTER starting the span: EnterChildLane reads Activity.Current, and the anchor
        // must be this invocation's span — not the surrounding Task.Execute — so multiple local
        // invocations inside one task each own their triggered work.
        // Restart the activation episode too: the client waiting on THIS instance does not observe
        // the triggered one, so the target's time-to-Active is measured from this invocation.
        using var lane = WorkflowTraceLane.EnterChildLane(TelemetryConstants.ActivationTriggers.Trigger);

        // A co-hosted target domain runs in-process: serve it as that domain.
        using var domainScope = DomainScope.Begin(GetTargetDomain(task));

        var result = await action(cancellationToken);

        if (activity is not null)
        {
            if (!result.IsSuccess)
            {
                activity.SetStatus(ActivityStatusCode.Error, result.Error.Message);
            }
            else if (result.Value is { IsSuccess: false } failure)
            {
                // Business failure: keep span status OK (flow continues via boundaries/auto
                // transitions) but record the status code for filtering.
                activity.SetTag("http.response.status_code", failure.StatusCode);
            }
        }

        return result;
    }

    /// <summary>
    /// Maps an <see cref="Error"/> to the equivalent HTTP status code based on its prefix.
    /// Used to ensure local execution failures carry the same status codes as remote (Dapr) execution.
    /// </summary>
    protected static int MapErrorToStatusCode(Error error)
        => ErrorNormalizer.MapPrefixToStatusCode(error.Prefix) ?? 500;

    /// <summary>
    /// The credential an instance read (GetInstance / GetInstances / GetInstanceData) runs as, same-domain and cross-domain
    /// alike, resolved by <see cref="HttpTaskInvocation.ResolveInstanceCallCredential"/>: a credential (<c>sub</c>,
    /// <c>act_sub</c>, <c>position</c>, <c>client_id</c>, <c>role</c>) the task's header mapping set — blank included — is the
    /// mapping's decision and is kept as-is; only an absent credential is filled from the caller's request headers. A blank
    /// credential stays in the result as <c>""</c> (a marker) and is dropped only at the exit. The caller's values come from
    /// its request headers, so a role a provider resolved for it (morph-idm) is never carried — the target resolves roles
    /// from the forwarded credential itself. The read's x-roles, x-masking and x-encryption are evaluated for this caller.
    /// </summary>
    protected static Dictionary<string, string?> BuildReadCredential(JsonElement? taskHeaders, ScriptContext? scriptContext)
        => HttpTaskInvocation.BuildOutgoingHeaders(
            ConvertTaskHeadersToDictionary(taskHeaders),
            scriptContext?.Headers is null ? null : scriptContext.GetHeadersAsDictionary());

    /// <summary>
    /// The same credential as <see cref="BuildReadCredential"/> (rule:
    /// <see cref="HttpTaskInvocation.ResolveInstanceCallCredential"/>), serialized as the remote binding's <c>Headers</c>:
    /// the Execution host copies these onto the cross-domain request, so the target domain evaluates the same caller a
    /// same-domain read would. A blank credential stays in the JSON as <c>""</c>; the remote invoker drops it when it
    /// builds the request.
    /// </summary>
    protected static string SerializeReadCredential(string? bindingHeadersJson, ScriptContext? scriptContext)
    {
        Dictionary<string, string?>? bindingHeaders = null;
        if (!string.IsNullOrWhiteSpace(bindingHeadersJson))
        {
            try
            {
                using var document = JsonDocument.Parse(bindingHeadersJson);
                bindingHeaders = HttpTaskInvocation.ReadHeaderObject(document.RootElement);
            }
            catch (JsonException)
            {
                bindingHeaders = null;
            }
        }

        return JsonSerializer.Serialize(HttpTaskInvocation.BuildOutgoingHeaders(
            bindingHeaders, scriptContext?.Headers is null ? null : scriptContext.GetHeadersAsDictionary()));
    }

    /// <summary>
    /// Makes <paramref name="credential"/> the current user for the read, the way the target domain's middleware would
    /// for the same headers. Blank credentials are removed from the dictionary in place (the same instance is the read
    /// input's headers). See <see cref="CallerCredentialScope"/>.
    /// </summary>
    protected static IDisposable ReadAs(ICurrentUser currentUser, Dictionary<string, string?> credential)
        => CallerCredentialScope.Enter(currentUser, credential);

    /// <summary>
    /// Converts task headers (JsonElement?) to Dictionary for local Input objects, through
    /// <see cref="HttpTaskInvocation.ReadHeaderObject"/>: one non-string value no longer discards the whole set (a blanked
    /// credential included). Not a JSON object → null.
    /// </summary>
    protected static Dictionary<string, string?>? ConvertTaskHeadersToDictionary(JsonElement? taskHeaders)
        => taskHeaders.HasValue ? HttpTaskInvocation.ReadHeaderObject(taskHeaders.Value) : null;
}

