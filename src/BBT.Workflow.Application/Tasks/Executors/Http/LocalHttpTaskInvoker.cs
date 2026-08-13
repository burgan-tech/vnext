using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// In-process implementation of <see cref="ILocalHttpTaskInvoker"/> for the Orchestration host.
/// A faithful port of the Execution service's <c>HttpTaskInvoker</c> (execution stays untouched):
/// any behavioral change here must be mirrored there, or the two HTTP task types drift apart.
/// Differences are deliberate and minimal — it returns the orchestrator-side
/// <see cref="TaskInvocationResult"/> directly (no wire DTO), and logs through
/// <c>WorkflowLogs</c> instead of the Execution metrics channel.
/// </summary>
public sealed class LocalHttpTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<LocalHttpTaskInvoker> logger) : ILocalHttpTaskInvoker
{
    /// <summary>
    /// Response-body parse options, matching the Execution invoker helpers: deep payloads are
    /// tolerated and cycles ignored rather than failing the task.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        MaxDepth = 256,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        HttpTaskBinding binding,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var httpClient = CreateHttpClient(binding, taskKey);
            var request = new HttpRequestMessage(
                new HttpMethod(binding.Method),
                binding.Url);

            // Add headers. Content-Type is a content header in .NET, not a request header, so it is
            // captured here and applied to request.Content below instead of request.Headers.
            string? headerContentType = null;
            if (!string.IsNullOrEmpty(binding.Headers))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Headers);
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            headerContentType = header.Value;
                            continue;
                        }

                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            // Add body for non-GET requests. Resolution: explicit ContentType → Content-Type header → json.
            if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(binding.Body))
            {
                var contentType = HttpContentType.Resolve(binding.ContentType, headerContentType);
                request.Content = new StringContent(binding.Body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }

            var response = await httpClient.SendAsync(request, cancellationToken);

            var responseHeaders = MergeHeaders(response.Headers, response.Content.Headers);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var responseData = TryParseJson(content);

            var metadata = new Dictionary<string, object>
            {
                ["Url"] = binding.Url,
                ["Method"] = binding.Method,
                ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty
            };

            var isSuccess = response.IsSuccessStatusCode
                || AcceptedStatusCodeMatcher.IsAccepted((int)response.StatusCode, binding.AcceptedStatusCodes);

            // Always return full response details (2xx, 4xx, 5xx alike) — the output mapping and the
            // error boundary decide what an error response means, exactly like the remote path.
            return new TaskInvocationResult
            {
                IsSuccess = isSuccess,
                StatusCode = (int)response.StatusCode,
                Body = content,
                Data = responseData,
                Headers = responseHeaders,
                Metadata = metadata,
                TaskType = TaskType.LocalHttp.ToString(),
                ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = isSuccess ? null : $"HTTP {response.StatusCode}: {response.ReasonPhrase}"
            };
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            logger.LocalHttpTaskRequestCancelled(taskKey, binding.Url);

            return TaskInvocationResult.Failure(
                error: "HTTP request was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType.LocalHttp.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = binding.Method,
                    ["Cancelled"] = true,
                    ["ExceptionType"] = nameof(TaskCanceledException)
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LocalHttpTaskRequestFailed(ex, taskKey, binding.Url, ex.Message);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType.LocalHttp.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = binding.Method,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }

    private HttpClient CreateHttpClient(HttpTaskBinding binding, string? taskKey)
    {
        var clientName = binding.ValidateSSL
            ? WorkflowHttpClientNames.Default
            : WorkflowHttpClientNames.NoSslValidation;

        if (!binding.ValidateSSL)
        {
            logger.LocalHttpTaskSslValidationDisabled(taskKey, binding.Url);
        }

        var client = httpClientFactory.CreateClient(clientName);
        client.Timeout = TimeSpan.FromSeconds(binding.TimeoutSeconds);
        return client;
    }

    /// <summary>
    /// Attempts to parse the response body as JSON, returning the raw string when it is not JSON —
    /// same tolerance as the Execution invoker helpers.
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

    /// <summary>
    /// Merges response and content headers case-insensitively, concatenating duplicate values —
    /// same shape the remote path returns.
    /// </summary>
    private static Dictionary<string, string> MergeHeaders(
        HttpResponseHeaders responseHeaders,
        HttpContentHeaders contentHeaders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in responseHeaders.Concat(contentHeaders))
        {
            var value = string.Join(", ", header.Value);
            result[header.Key] = result.TryGetValue(header.Key, out var existing)
                ? $"{existing}, {value}"
                : value;
        }

        return result;
    }
}
