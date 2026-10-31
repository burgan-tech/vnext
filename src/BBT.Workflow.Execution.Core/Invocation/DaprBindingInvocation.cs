using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using Dapr.Client;
using Grpc.Core;

namespace BBT.Workflow.Execution.Core.Invocation;

/// <summary>
/// The single implementation of a Dapr output-binding task call, shared by the Execution host's
/// <c>DaprBindingTaskInvoker</c> and the Orchestrator's in-process path, so the two cannot drift. Unlike the former
/// fire-and-forget call it returns the binding's answer: <c>get</c> (an object), <c>list</c> (the keys),
/// <c>delete</c>, <c>presign</c> are only useful with it (vnext-client-sdk-core#142-B).
/// <list type="bullet">
/// <item><see cref="TaskInvocationResult.Data"/>: the response body as JSON when it parses, else UTF-8 text when it is
/// text, else base64; <c>Metadata["DataEncoding"]</c> says which (<c>json</c>, <c>text</c>, <c>base64</c>, <c>none</c>).
/// <see cref="TaskInvocationResult.Body"/> carries the raw text for <c>json</c>/<c>text</c>.</item>
/// <item><see cref="TaskInvocationResult.Headers"/>: the binding's response metadata.</item>
/// <item>A failure carries the sidecar's reason (the gRPC status detail, e.g. <c>couldn't find output binding x</c>)
/// instead of the client's generic "the Dapr endpoint indicated a failure".</item>
/// </list>
/// No logging or metrics here: every outcome is returned, and the hosts log and meter.
/// </summary>
public static class DaprBindingInvocation
{
    private static readonly ActivitySource InvokerActivitySource = new("BBT.Workflow.Execution.Invokers");

    /// <summary>Invokes the binding; never throws.</summary>
    public static async Task<TaskInvocationResult> ExecuteAsync(
        DaprClient daprClient,
        DaprBindingTaskBinding binding,
        string taskType,
        CancellationToken cancellationToken,
        string? taskKey = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var prepareActivity = InvokerActivitySource.StartActivity("Invoke.Prepare", ActivityKind.Internal);
        prepareActivity?.SetTag("vnext.task.key", taskKey ?? string.Empty);
        prepareActivity?.SetTag("vnext.task.type", taskType);

        var metadata = binding.Metadata ?? new Dictionary<string, string>();
        // The task's "method" metadata overrides its operation.
        var operation = metadata.TryGetValue("method", out var method) && !string.IsNullOrEmpty(method)
            ? method
            : binding.Operation;

        try
        {
            var request = new BindingRequest(binding.BindingName, operation);
            foreach (var (key, value) in metadata)
                if (key is not ("method" or "ForwardingHeaders"))
                    request.Metadata[key] = value;

            // Bindings bypass HttpClient's DiagnosticsHandler: propagate the LIVE trace context explicitly.
            if (Activity.Current is { } current)
            {
                request.Metadata["traceparent"] = current.Id!;
                if (!string.IsNullOrEmpty(current.TraceStateString))
                    request.Metadata["tracestate"] = current.TraceStateString;
            }

            // The task body is JSON text; it goes to the binding as those bytes (what the JSON serializer sent before).
            if (!string.IsNullOrEmpty(binding.Body))
                request.Data = Encoding.UTF8.GetBytes(binding.Body);

            prepareActivity?.Dispose();
            var response = await daprClient.InvokeBindingAsync(request, cancellationToken).ConfigureAwait(false);

            var (data, body, encoding) = Shape(response.Data);
            return TaskInvocationResult.Success(
                data: data,
                body: body,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                headers: response.Metadata.Count == 0 ? null : new Dictionary<string, string>(response.Metadata),
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = binding.BindingName,
                    ["Operation"] = operation,
                    ["DataEncoding"] = encoding,
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            prepareActivity?.Dispose();
            return TaskInvocationResult.Failure(
                error: "Dapr binding invocation was cancelled",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = binding.BindingName,
                    ["Operation"] = operation,
                    ["Cancelled"] = true,
                });
        }
        catch (Exception ex)
        {
            prepareActivity?.Dispose();
            return TaskInvocationResult.Failure(
                error: Reason(ex),
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = binding.BindingName,
                    ["Operation"] = operation,
                    ["ExceptionType"] = ex.GetType().Name,
                });
        }
    }

    /// <summary>The sidecar's own reason when the failure carries a gRPC status, else the exception message.</summary>
    internal static string Reason(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            if (e is RpcException rpc && !string.IsNullOrWhiteSpace(rpc.Status.Detail))
                return $"Binding operation failed: {rpc.Status.Detail} ({rpc.StatusCode})";
        return ex.Message;
    }

    /// <summary>JSON when it parses (an object, array or scalar), else text when it is valid UTF-8 text, else base64.</summary>
    internal static (object? Data, string? Body, string Encoding) Shape(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
            return (null, null, "none");

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return (document.RootElement.Clone(), Encoding.UTF8.GetString(bytes.Span), "json");
        }
        catch (JsonException) { }

        if (IsText(bytes.Span, out var text))
            return (text, text, "text");

        return (Convert.ToBase64String(bytes.Span), null, "base64");
    }

    private static bool IsText(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        foreach (var c in text)
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
                return false;
        return true;
    }
}
