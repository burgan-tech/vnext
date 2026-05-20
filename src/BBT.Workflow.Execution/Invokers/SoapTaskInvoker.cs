using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// SOAP task invoker — stateless execution with strongly-typed binding.
/// Handles SOAP 1.1 and 1.2 protocol differences for Content-Type and SOAPAction headers,
/// parses XML responses, and detects SOAP Fault elements.
/// </summary>
public sealed class SoapTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<SoapTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<SoapTaskBinding>
{
    private static readonly XNamespace Soap11Ns = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Soap12Ns = "http://www.w3.org/2003/05/soap-envelope";

    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <inheritdoc />
    public string TaskType => TaskTypes.Soap;

    /// <inheritdoc />
    public Type BindingType => typeof(SoapTaskBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<SoapTaskBinding> descriptor,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(descriptor.TaskKey, descriptor.Binding, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<SoapTaskBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize SoapTaskBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        SoapTaskBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var httpClient = CreateHttpClient(binding, taskKey);
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
                    foreach (var header in headers)
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var response = await httpClient.SendAsync(request, cancellationToken);

            var responseHeaders = InvokerHelpers.MergeHeaders(response.Headers, response.Content.Headers);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

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

            return response.IsSuccessStatusCode
                ? TaskInvocationResult.Success(
                    data: parsedData,
                    body: content,
                    statusCode: (int)response.StatusCode,
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: TaskType,
                    headers: responseHeaders,
                    metadata: metadata)
                : TaskInvocationResult.Failure(
                    error: isSoapFault
                        ? $"SOAP Fault: {faultString ?? faultCode ?? "Unknown fault"}"
                        : $"HTTP {response.StatusCode}: {response.ReasonPhrase}",
                    statusCode: (int)response.StatusCode,
                    body: content,
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: TaskType,
                    headers: responseHeaders,
                    data: parsedData,
                    metadata: metadata);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            logger.LogWarning("SOAP request was cancelled for task {TaskKey} - URL: {Url}", taskKey, binding.Url);

            return TaskInvocationResult.Failure(
                error: "SOAP request was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = "POST",
                    ["Cancelled"] = true,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            logger.LogError(ex, "SOAP task invocation failed for {TaskKey} - URL: {Url}", taskKey, binding.Url);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = "POST",
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            logger.LogError(ex, "Unexpected error during SOAP task invocation for {TaskKey}", taskKey);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = "POST",
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }

    /// <summary>
    /// Parses the raw XML SOAP response. Returns structured data extracted from the Body element,
    /// plus SOAP Fault details if present.
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

    private HttpClient CreateHttpClient(SoapTaskBinding binding, string? taskKey)
    {
        var clientName = binding.ValidateSSL
            ? WorkflowHttpClientNames.Default
            : WorkflowHttpClientNames.NoSslValidation;

        if (!binding.ValidateSSL)
        {
            logger.LogDebug("SSL certificate validation is disabled for SOAP task {TaskKey} - URL: {Url}",
                taskKey, binding.Url);
        }

        var client = httpClientFactory.CreateClient(clientName);
        client.Timeout = TimeSpan.FromSeconds(binding.TimeoutSeconds);
        return client;
    }
}
