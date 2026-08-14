using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Execution.Bindings;

namespace BBT.Workflow.Execution;

/// <summary>
/// The single implementation of an HTTP task call, shared by both hosts: the Execution service's
/// <c>HttpTaskInvoker</c> (type 6, reached through <c>/execution/invoke</c>) and the Orchestrator's
/// <c>ExternalHttpTaskInvoker</c> (type 21, in-process) delegate here, so the two task types cannot
/// drift behaviorally — header/Content-Type splitting, body resolution, named-client selection by
/// <c>validateSsl</c>, per-request timeout, response parsing and accepted-status-code matching are
/// defined exactly once.
/// <para>
/// Lives in the abstractions assembly because it is the only project both hosts reference. To keep
/// this assembly package-free ("contracts only"), the named-client resolution is taken as a
/// <c>Func&lt;string, HttpClient&gt;</c> — hosts pass <c>IHttpClientFactory.CreateClient</c> — and no
/// logging or metrics happen here: every outcome, including cancellation and transport failure, is
/// returned as a <see cref="TaskInvocationResult"/> whose metadata carries what the hosts need to
/// log (<c>Cancelled</c>, <c>ExceptionType</c>, <c>StackTrace</c>).
/// </para>
/// </summary>
public static class HttpTaskInvocation
{
    /// <summary>
    /// Response-body parse options: deep payloads are tolerated and cycles ignored rather than
    /// failing the task.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        MaxDepth = 256,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    /// <summary>
    /// Executes the HTTP request described by the binding. Never throws: transport failures and
    /// cancellation become failed results so the caller's error boundary decides.
    /// </summary>
    /// <param name="createClient">Named-client resolver, normally <c>IHttpClientFactory.CreateClient</c>.
    /// The name is chosen from <see cref="WorkflowHttpClientNames"/> by the binding's <c>ValidateSSL</c>.</param>
    /// <param name="binding">The prepared HTTP binding (URL, method, headers, body, options).</param>
    /// <param name="taskType">Task-type label stamped on the result (each host stamps its own).</param>
    /// <param name="cancellationToken">Caller cancellation; a fire during the request yields a
    /// failed result with <c>Cancelled = true</c> metadata.</param>
    public static async Task<TaskInvocationResult> SendAsync(
        Func<string, HttpClient> createClient,
        HttpTaskBinding binding,
        string taskType,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var clientName = binding.ValidateSSL
                ? WorkflowHttpClientNames.Default
                : WorkflowHttpClientNames.NoSslValidation;
            var httpClient = createClient(clientName);
            httpClient.Timeout = TimeSpan.FromSeconds(binding.TimeoutSeconds);

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

            // Always return result with full response details - let output mapping handle error scenarios
            // All HTTP responses (2xx, 4xx, 5xx) include headers, body, and parsed data
            return isSuccess
                ? TaskInvocationResult.Success(
                    data: responseData,
                    body: content,
                    statusCode: (int)response.StatusCode,
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: taskType,
                    headers: responseHeaders,
                    metadata: metadata)
                : TaskInvocationResult.Failure(
                    error: $"HTTP {response.StatusCode}: {response.ReasonPhrase}",
                    statusCode: (int)response.StatusCode,
                    body: content,
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: taskType,
                    headers: responseHeaders,
                    data: responseData,
                    metadata: metadata);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            return TaskInvocationResult.Failure(
                error: "HTTP request was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = binding.Method,
                    ["Cancelled"] = true,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = binding.Method,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }

    /// <summary>Whether a failed result represents a cancellation, for host-side logging/metrics.</summary>
    public static bool WasCancelled(TaskInvocationResult result) =>
        result.Metadata?.TryGetValue("Cancelled", out var flag) == true && flag is true;

    /// <summary>
    /// Attempts to parse JSON content. Returns the original content if parsing fails.
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
    /// Merges response headers and content headers into a single dictionary.
    /// Uses case-insensitive key comparison and concatenates duplicate header values.
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
