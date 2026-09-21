using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BBT.Workflow.Execution.Bindings;

namespace BBT.Workflow.Execution.Core.Invocation;

/// <summary>
/// The single implementation of a SOAP task call, shared by both hosts: the Execution service's
/// <c>SoapTaskInvoker</c> and (from a later task) the Orchestrator's in-process path delegate
/// here, so the two cannot drift behaviorally — SOAP 1.1/1.2 Content-Type and SOAPAction handling,
/// header filtering, correlation-header stamping, XML response parsing and Fault detection, and
/// accepted-status-code matching are defined exactly once.
/// <para>
/// Lives in <c>Execution.Core</c> because the Orchestration host cannot reference
/// <c>BBT.Workflow.Execution</c> (pythonnet, KubernetesClient, Dapr.AI, a hosted service) and the
/// Abstractions assembly stays contract-only. This project carries only the packages both hosts
/// already accept. Mirrors <c>HttpTaskInvocation</c> and <c>DaprServiceInvocation</c> exactly: no
/// logging or metrics happen here — every outcome, including a SOAP Fault, cancellation and
/// transport failure, is returned as a <see cref="TaskInvocationResult"/> whose metadata carries
/// what the hosts need to log (<c>Cancelled</c>, <c>ExceptionType</c>).
/// </para>
/// </summary>
public static class SoapInvocation
{
    private static readonly XNamespace Soap11Ns = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Soap12Ns = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>
    /// Deliberately the SAME source name as the Execution host's <c>InvokerActivityHelper</c> and
    /// as <c>HttpTaskInvocation</c> / <c>DaprServiceInvocation</c> (ActivitySource listeners match
    /// by name): the <c>Invoke.Prepare</c> span this core starts is the same span family the
    /// host's other invokers use, and the Execution host's <c>BBT.Workflow.Execution*</c>
    /// AdditionalSources wildcard exports it without new config. The helper itself lives in the
    /// host assembly, which this project cannot reference.
    /// </summary>
    private static readonly ActivitySource InvokerActivitySource = new("BBT.Workflow.Execution.Invokers");

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
    /// Executes the SOAP request described by the binding. Never throws: transport failures and
    /// cancellation become failed results so the caller's error boundary decides. A SOAP Fault in
    /// an otherwise-successful HTTP response is also reported as a failed result, exactly as the
    /// Execution host's <c>SoapTaskInvoker</c> does.
    /// </summary>
    /// <param name="createClient">Named-client resolver, normally <c>IHttpClientFactory.CreateClient</c>.
    /// The name is chosen from <see cref="WorkflowHttpClientNames"/> by the binding's <c>ValidateSSL</c>.</param>
    /// <param name="binding">The prepared SOAP binding (URL, version, action, headers, body, options).</param>
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
        Func<string, HttpClient> createClient,
        SoapTaskBinding binding,
        string taskType,
        CancellationToken cancellationToken,
        TaskTraceContext? trusted = null,
        string? taskKey = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var prepareActivity = StartPrepareActivity(taskType, taskKey);

        try
        {
            var clientName = binding.ValidateSSL
                ? WorkflowHttpClientNames.Default
                : WorkflowHttpClientNames.NoSslValidation;
            var httpClient = createClient(clientName);
            httpClient.Timeout = TimeSpan.FromSeconds(binding.TimeoutSeconds);

            var request = new HttpRequestMessage(HttpMethod.Post, binding.Url);

            // Set SOAP-version-specific Content-Type and SOAPAction
            var isSoap12 = string.Equals(binding.SoapVersion, "1.2", StringComparison.OrdinalIgnoreCase);

            if (isSoap12)
            {
                var mediaType = string.IsNullOrEmpty(binding.SoapAction)
                    ? "application/soap+xml; charset=utf-8"
                    : $"application/soap+xml; charset=utf-8; action=\"{binding.SoapAction}\"";
                request.Content = new StringContent(binding.Body ?? string.Empty, Encoding.UTF8);
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType);
            }
            else
            {
                // SOAP 1.1: text/xml + separate SOAPAction header
                request.Content = new StringContent(binding.Body ?? string.Empty, Encoding.UTF8, "text/xml");

                if (!string.IsNullOrEmpty(binding.SoapAction))
                    request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{binding.SoapAction}\"");
            }

            // Add any additional HTTP headers (e.g. Authorization)
            if (!string.IsNullOrEmpty(binding.Headers))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Headers);
                if (headers != null)
                {
                    foreach (var header in headers.Where(h => !HttpTaskInvocation.IsReservedTraceHeader(h.Key)))
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            HttpTaskInvocation.ApplyTrustedCorrelationHeaders(request, trusted);

            prepareActivity?.Dispose();
            var response = await httpClient.SendAsync(request, cancellationToken);

            var responseHeaders = InvocationHelpers.MergeHeaders(response.Headers, response.Content.Headers);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            var (parsedData, isSoapFault, faultCode, faultString) = TryParseSoapResponse(content, isSoap12);

            var metadata = new Dictionary<string, object>
            {
                ["Url"] = binding.Url,
                ["Method"] = "POST",
                ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty,
                ["SoapVersion"] = binding.SoapVersion,
                ["IsSoapFault"] = isSoapFault
            };

            if (isSoapFault)
            {
                if (faultCode != null) metadata["SoapFaultCode"] = faultCode;
                if (faultString != null) metadata["SoapFaultString"] = faultString;
            }

            var isSuccess = response.IsSuccessStatusCode
                || AcceptedStatusCodeMatcher.IsAccepted((int)response.StatusCode, binding.AcceptedStatusCodes);

            return isSuccess
                ? TaskInvocationResult.Success(
                    data: parsedData,
                    body: content,
                    statusCode: (int)response.StatusCode,
                    executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    taskType: taskType,
                    headers: responseHeaders,
                    metadata: metadata)
                : TaskInvocationResult.Failure(
                    error: isSoapFault
                        ? $"SOAP Fault: {faultString ?? faultCode ?? "Unknown fault"}"
                        : $"HTTP {response.StatusCode}: {response.ReasonPhrase}",
                    statusCode: (int)response.StatusCode,
                    body: content,
                    executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    taskType: taskType,
                    headers: responseHeaders,
                    data: parsedData,
                    metadata: metadata);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            prepareActivity?.Dispose();
            return TaskInvocationResult.Failure(
                error: "SOAP request was cancelled",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = "POST",
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
                    ["Url"] = binding.Url,
                    ["Method"] = "POST",
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
    }

    /// <summary>
    /// Parses the raw XML SOAP response. Returns structured data extracted from the Body element,
    /// plus SOAP Fault details if present. Copied (not shared) from the Execution host's
    /// <c>SoapTaskInvoker</c> — see <see cref="SendAsync"/>'s remarks for why the host assembly is
    /// not reachable from this project.
    /// </summary>
    private static (object? Data, bool IsSoapFault, string? FaultCode, string? FaultString)
        TryParseSoapResponse(string content, bool isSoap12)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, false, null, null);

        try
        {
            var doc = XDocument.Parse(content);
            var ns = isSoap12 ? Soap12Ns : Soap11Ns;

            var body = doc.Descendants(ns + "Body").FirstOrDefault();
            if (body == null)
                return (XmlElementToDictionary(doc.Root), false, null, null);

            // Detect SOAP Fault
            var fault = body.Element(ns + "Fault");
            if (fault != null)
            {
                string? faultCode = null;
                string? faultString = null;

                if (isSoap12)
                {
                    faultCode = fault.Element(ns + "Code")?.Element(ns + "Value")?.Value;
                    faultString = fault.Element(ns + "Reason")?.Element(ns + "Text")?.Value;
                }
                else
                {
                    faultCode = fault.Element("faultcode")?.Value;
                    faultString = fault.Element("faultstring")?.Value;
                }

                var faultData = XmlElementToDictionary(fault);
                return (faultData, true, faultCode, faultString);
            }

            // Return body content as structured dictionary
            var firstChild = body.Elements().FirstOrDefault();
            var data = firstChild != null ? XmlElementToDictionary(firstChild) : XmlElementToDictionary(body);
            return (data, false, null, null);
        }
        catch
        {
            // Non-parseable XML — return raw string as body only
            return (null, false, null, null);
        }
    }

    /// <summary>
    /// Converts an XElement tree to a nested Dictionary for use as structured task output data.
    /// </summary>
    private static object XmlElementToDictionary(XElement? element)
    {
        if (element == null)
            return new Dictionary<string, object>();

        // Leaf node: return text value
        if (!element.HasElements)
            return element.Value;

        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in element.Elements())
        {
            var localName = child.Name.LocalName;
            var value = XmlElementToDictionary(child);

            if (dict.TryGetValue(localName, out var existing))
            {
                // Multiple elements with same name → collect as list
                if (existing is List<object> list)
                    list.Add(value);
                else
                    dict[localName] = new List<object> { existing, value };
            }
            else
            {
                dict[localName] = value;
            }
        }

        return dict;
    }

}
