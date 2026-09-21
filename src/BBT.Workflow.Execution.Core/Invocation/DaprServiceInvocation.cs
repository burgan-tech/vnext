using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;

namespace BBT.Workflow.Execution.Core.Invocation;

/// <summary>
/// The single implementation of a Dapr service-invocation call, shared by both hosts: the
/// Execution service's <c>DaprServiceTaskInvoker</c> and (from a later task) the Orchestrator's
/// in-process path delegate here, so the two cannot drift behaviorally — request construction,
/// query-string merging, body, header filtering, correlation-header stamping, response parsing
/// and accepted-status-code matching are defined exactly once.
/// <para>
/// Lives in <c>Execution.Core</c> because the Orchestration host cannot reference
/// <c>BBT.Workflow.Execution</c> (pythonnet, KubernetesClient, Dapr.AI, a hosted service) and the
/// Abstractions assembly stays contract-only. This project carries only the packages both hosts
/// already accept. Mirrors <c>HttpTaskInvocation</c> exactly: no logging or metrics happen here —
/// every outcome, including cancellation and transport failure, is returned as a
/// <see cref="TaskInvocationResult"/> whose metadata carries what the hosts need to log
/// (<c>Cancelled</c>, <c>ExceptionType</c>).
/// </para>
/// </summary>
public static class DaprServiceInvocation
{
    /// <summary>
    /// Deliberately the SAME source name as the Execution host's <c>InvokerActivityHelper</c> and
    /// as <c>HttpTaskInvocation</c> (ActivitySource listeners match by name): the
    /// <c>Invoke.Prepare</c> span this core starts is the same span family the host's other
    /// invokers use, and the Execution host's <c>BBT.Workflow.Execution*</c> AdditionalSources
    /// wildcard exports it without new config. The helper itself lives in the host assembly,
    /// which this project cannot reference.
    /// </summary>
    private static readonly ActivitySource InvokerActivitySource = new("BBT.Workflow.Execution.Invokers");

    /// <summary>
    /// Response-body parse options: deep payloads are tolerated and cycles ignored rather than
    /// failing the task.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        MaxDepth = 256,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    /// <summary>
    /// Starts the span covering everything this core does BEFORE the outbound call — client
    /// construction, header/URL/body preparation. Disposed immediately before the I/O call so the
    /// trace separates "our prep" from "their latency" (mirrors the Execution host's
    /// <c>InvokerActivityHelper.StartPrepareActivity</c> and <c>HttpTaskInvocation</c>'s copy).
    /// </summary>
    private static Activity? StartPrepareActivity(string taskType, string? taskKey)
    {
        var activity = InvokerActivitySource.StartActivity("Invoke.Prepare", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("vnext.task.key", taskKey ?? string.Empty);
            activity.SetTag("vnext.task.type", taskType);
        }
        return activity;
    }

    /// <summary>
    /// Executes the Dapr service invocation described by the binding. Never throws: transport
    /// failures and cancellation become failed results so the caller's error boundary decides.
    /// </summary>
    /// <param name="client">The shared Dapr service-invocation client (targets the local sidecar).</param>
    /// <param name="binding">The prepared Dapr service binding (app-id, method, headers, body, options).</param>
    /// <param name="taskType">Task-type label stamped on the result (each host stamps its own).</param>
    /// <param name="cancellationToken">Caller cancellation; a fire during the request yields a
    /// failed result with <c>Cancelled = true</c> metadata.</param>
    /// <param name="trusted">Explicit workflow correlation source for
    /// <see cref="HttpTaskInvocation.ApplyTrustedCorrelationHeaders"/>. The Orchestrator's
    /// in-process path MUST pass it: ambient Activity baggage is not reliable there, because
    /// intermediate task spans are created from <see cref="System.Diagnostics.ActivityContext"/>
    /// which severs the managed parent chain baggage lookups walk. The Execution host passes
    /// null — its request activity carries the values restored from the invoke envelope.</param>
    /// <param name="taskKey">Task key, used only for the prepare span's tag.</param>
    public static async Task<TaskInvocationResult> SendAsync(
        DaprServiceInvocationClient client,
        DaprServiceBinding binding,
        string taskType,
        CancellationToken cancellationToken,
        TaskTraceContext? trusted = null,
        string? taskKey = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var prepareActivity = StartPrepareActivity(taskType, taskKey);

        try
        {
            var request = DaprServiceInvocationClient.CreateRequest(
                new HttpMethod(binding.Method),
                binding.AppId,
                binding.MethodName);

            // Add query string
            if (!string.IsNullOrEmpty(binding.QueryString))
            {
                var uriBuilder = new UriBuilder(request.RequestUri!);
                var queryString = binding.QueryString.TrimStart('?');
                uriBuilder.Query = string.IsNullOrEmpty(uriBuilder.Query)
                    ? queryString
                    : uriBuilder.Query.TrimStart('?') + "&" + queryString;
                request.RequestUri = uriBuilder.Uri;
            }

            // Add body for non-GET requests
            if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(binding.Body))
            {
                request.Content = new StringContent(binding.Body, Encoding.UTF8, "application/json");
            }

            // Add headers
            if (!string.IsNullOrEmpty(binding.Headers))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Headers);
                if (headers != null)
                {
                    foreach (var header in headers.Where(h =>
                                 h.Value != null && !HttpTaskInvocation.IsReservedTraceHeader(h.Key)))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            HttpTaskInvocation.ApplyTrustedCorrelationHeaders(request, trusted);

            // SendAsync performs no status validation: every 2xx/4xx/5xx comes back as a response, so
            // status codes reach output mapping (parity with the obsolete InvokeMethodWithResponseAsync)
            prepareActivity?.Dispose();
            using var response = await client.SendAsync(request, cancellationToken);

            var responseHeaders = InvocationHelpers.MergeHeaders(response.Headers, response.Content.Headers);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var responseData = TryParseJson(content);

            var metadata = new Dictionary<string, object>
            {
                ["AppId"] = binding.AppId,
                ["MethodName"] = binding.MethodName,
                ["HttpVerb"] = binding.Method,
                ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty
            };

            var isSuccess = response.IsSuccessStatusCode
                || AcceptedStatusCodeMatcher.IsAccepted((int)response.StatusCode, binding.AcceptedStatusCodes);

            // Always return result with full response details - let output mapping handle error scenarios
            // All HTTP responses (2xx, 4xx, 5xx) include headers, body, and parsed data
            return isSuccess
                ? TaskInvocationResult.Success(
                    data: responseData,
                    body: content,
                    statusCode: (int)response.StatusCode,
                    executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    taskType: taskType,
                    headers: responseHeaders,
                    metadata: metadata)
                : TaskInvocationResult.Failure(
                    error: $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    statusCode: (int)response.StatusCode,
                    body: content,
                    executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    taskType: taskType,
                    headers: responseHeaders,
                    data: responseData,
                    metadata: metadata);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            prepareActivity?.Dispose();
            return TaskInvocationResult.Failure(
                error: "Dapr service invocation was cancelled",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["AppId"] = binding.AppId,
                    ["MethodName"] = binding.MethodName,
                    ["HttpVerb"] = binding.Method,
                    ["Cancelled"] = true,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (Exception ex)
        {
            prepareActivity?.Dispose();
            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["AppId"] = binding.AppId,
                    ["MethodName"] = binding.MethodName,
                    ["HttpVerb"] = binding.Method,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
    }

    /// <summary>
    /// Attempts to parse JSON content. Returns the original content if parsing fails. Copied
    /// (not shared) from the Execution host's internal <c>InvokerHelpers.TryParseJson</c> — the
    /// host assembly is not reachable from this project, and <c>HttpTaskInvocation</c> keeps its
    /// own private copy for the same reason.
    /// </summary>
    private static object? TryParseJson(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        try
        {
            return JsonSerializer.Deserialize<object>(content, ResponseJsonOptions);
        }
        catch (JsonException)
        {
            return content;
        }
    }

}
