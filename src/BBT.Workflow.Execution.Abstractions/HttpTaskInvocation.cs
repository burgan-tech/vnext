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
/// <c>ExternalHttpTaskInvoker</c> (type 22, in-process) delegate here, so the two task types cannot
/// drift behaviorally — header/Content-Type splitting, body resolution, named-client selection by
/// <c>validateSsl</c>, per-request timeout, response parsing and accepted-status-code matching are
/// defined exactly once.
/// <para>
/// Lives in the abstractions assembly because it is the only project both hosts reference. To keep
/// this assembly package-free ("contracts only"), the named-client resolution is taken as a
/// <c>Func&lt;string, HttpClient&gt;</c> — hosts pass <c>IHttpClientFactory.CreateClient</c> — and no
/// logging or metrics happen here: every outcome, including cancellation and transport failure, is
/// returned as a <see cref="TaskInvocationResult"/> whose metadata carries what the hosts need to
/// log (<c>Cancelled</c>, <c>ExceptionType</c>). Stack traces are deliberately NOT placed in
/// metadata: it travels into task results, the execution journal and output mappings, so it
/// could be persisted or surface to API clients.
/// </para>
/// </summary>
public static class HttpTaskInvocation
{
    // Kept local so this contracts package does not acquire a Domain dependency.
    // These names are the public HTTP contract defined by TelemetryConstants in vNext Domain.
    private const string WorkflowInstanceHeader = "X-Workflow-Instance-Id";
    private const string CorrelationHeader = "X-Correlation-Id";
    private const string SubHeader = "sub";
    private const string ActSubHeader = "act_sub";
    private const string RequestIdHeader = "X-Request-Id";
    private const string WorkflowInstanceBaggage = "workflow.instance.id";
    private const string CorrelationBaggage = "correlation.id";
    private const string SubBaggage = "sub";
    private const string ActSubBaggage = "act.sub";
    private const string RequestIdBaggage = "x_request_id";

    /// <summary>
    /// Header names owned by the tracing/correlation infrastructure. Task binding definitions
    /// must never overwrite these on outbound calls: a stale traceparent copied into a binding
    /// would detach the downstream service from the live trace, and a forged workflow-context
    /// header would spoof workflow identity. The live values are injected by HttpClient's
    /// DiagnosticsHandler (traceparent/tracestate) and by <see cref="ApplyTrustedCorrelationHeaders"/>.
    /// The identity claims (sub/act_sub) and <c>X-Request-Id</c> are deliberately NOT reserved: a
    /// developer may set them in the task binding and that value wins — see
    /// <see cref="ApplyTrustedCorrelationHeaders"/>. <c>X-Request-Id</c> was reserved between
    /// v0.0.80 and the fix, which silently dropped it from calls to APIs that REQUIRE it
    /// (OHVPS/BKM answer 400 <c>TR.OHVPS.Resource.InvalidFormat</c> without it).
    /// </summary>
    private static readonly string[] ReservedTraceHeaders =
    [
        "traceparent", "tracestate", "baggage",
        WorkflowInstanceHeader, CorrelationHeader
    ];

    /// <summary>
    /// Returns true when the header name is reserved for trace/correlation propagation and must
    /// not be copied from a task binding's header definition onto an outbound request. Public so
    /// the Execution host's non-HTTP invokers (Dapr, SOAP, direct trigger) apply the same guard
    /// via <c>InvokerHelpers</c>.
    /// </summary>
    public static bool IsReservedTraceHeader(string headerName) =>
        ReservedTraceHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase);

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
    /// Deliberately the SAME source name as the Execution host's <c>InvokerActivityHelper</c>
    /// (ActivitySource listeners match by name): the <c>Invoke.Prepare</c> span this core starts is
    /// the same span that helper starts for the non-HTTP invokers, and the Execution host's
    /// <c>BBT.Workflow.Execution*</c> AdditionalSources wildcard exports both without new config.
    /// The helper itself lives in the host assembly, which this contracts package cannot reference.
    /// </summary>
    private static readonly ActivitySource InvokerActivitySource = new("BBT.Workflow.Execution.Invokers");

    /// <summary>
    /// Starts the span covering everything this core does BEFORE the outbound call — client
    /// construction, header/URL/body preparation. Disposed immediately before the I/O call so the
    /// trace separates "our prep" from "their latency" (mirrors the Execution host's
    /// <c>InvokerActivityHelper.StartPrepareActivity</c>).
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
    /// Executes the HTTP request described by the binding. Never throws: transport failures and
    /// cancellation become failed results so the caller's error boundary decides.
    /// </summary>
    /// <param name="createClient">Named-client resolver, normally <c>IHttpClientFactory.CreateClient</c>.
    /// The name is chosen from <see cref="WorkflowHttpClientNames"/> by the binding's <c>ValidateSSL</c>.</param>
    /// <param name="binding">The prepared HTTP binding (URL, method, headers, body, options).</param>
    /// <param name="taskType">Task-type label stamped on the result (each host stamps its own).</param>
    /// <param name="cancellationToken">Caller cancellation; a fire during the request yields a
    /// failed result with <c>Cancelled = true</c> metadata.</param>
    /// <param name="traceContext">Explicit workflow correlation source for
    /// <see cref="ApplyTrustedCorrelationHeaders"/>. The Orchestrator's in-process task (type 22)
    /// MUST pass it: ambient Activity baggage is not reliable there, because intermediate task
    /// spans are created from <see cref="System.Diagnostics.ActivityContext"/> which severs the
    /// managed parent chain baggage lookups walk. The Execution host passes null — its request
    /// activity carries the values restored from the invoke envelope.</param>
    public static async Task<TaskInvocationResult> SendAsync(
        Func<string, HttpClient> createClient,
        HttpTaskBinding binding,
        string taskType,
        CancellationToken cancellationToken = default,
        TaskTraceContext? traceContext = null,
        string? taskKey = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            var (httpClient, request) = PrepareRequest(createClient, binding, taskType, traceContext, taskKey);

            var response = await httpClient.SendAsync(request, cancellationToken);

            var responseHeaders = MergeHeaders(response.Headers, response.Content.Headers);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
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
                    executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    taskType: taskType,
                    headers: responseHeaders,
                    metadata: metadata)
                : TaskInvocationResult.Failure(
                    error: $"HTTP {response.StatusCode}: {response.ReasonPhrase}",
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
            return TaskInvocationResult.Failure(
                error: "HTTP request was cancelled",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
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
            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = binding.Method,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
    }

    /// <summary>Whether a failed result represents a cancellation, for host-side logging/metrics.</summary>
    public static bool WasCancelled(TaskInvocationResult result) =>
        result.Metadata?.TryGetValue("Cancelled", out var flag) == true && flag is true;

    /// <summary>
    /// Stamps the trusted workflow correlation headers onto an outbound HTTP request. Shared by
    /// both HTTP task types (6 remote, 22 orchestrator-executed) via <see cref="SendAsync"/>, and
    /// by the Execution host's other invokers (Dapr, SOAP, direct trigger) via
    /// <c>InvokerHelpers</c>. Two precedence rules:
    /// <list type="bullet">
    /// <item><b>Workflow context</b> (X-Workflow-Instance-Id, X-Correlation-Id) is authoritative:
    /// mapping-provided values must never spoof the context established by vNext, so any
    /// pre-existing values are removed and the trusted values stamped.</item>
    /// <item><b>Identity claims</b> (sub, act_sub) are fill-if-absent: they are token-derived
    /// claims the platform provides as a DEFAULT — a developer who set them explicitly in the
    /// task binding's input mapping keeps their value; only when the binding did not set them
    /// are they filled from the gateway token. With <paramref name="fillIdentity"/> <c>false</c>
    /// this fill is skipped entirely: the Execution host's DirectTrigger and GetInstance /
    /// GetInstances / GetInstanceData invokers pass it, because for those instance calls the
    /// orchestrator is the single resolver of the credential
    /// (<see cref="ResolveInstanceCallCredential"/>) and the caller's value never comes from
    /// Activity baggage. The default <c>true</c> keeps HTTP, SOAP and Dapr tasks as they were.</item>
    /// <item><b>X-Request-Id</b> is fill-if-absent too: a non-empty value from the binding's
    /// input mapping wins; when the binding left it absent or empty, vNext's own request id (the
    /// originating request's <c>X-Request-Id</c>, as the correlation provider holds it) is sent
    /// unchanged. Nothing is generated when vNext has none.</item>
    /// </list>
    /// Value source per field: <paramref name="trusted"/> (the pipeline-built
    /// <see cref="TaskTraceContext"/>, same object the type-6 invoke envelope carries) wins when
    /// it supplies the field; ambient Activity baggage is the fallback. The fallback is what the
    /// Execution host uses — its request activity carries the envelope-restored values — while
    /// the Orchestrator's in-process path must pass <paramref name="trusted"/> because its task
    /// spans are created from <see cref="System.Diagnostics.ActivityContext"/>, which severs the
    /// managed parent chain that in-process baggage lookups walk.
    /// </summary>
    public static void ApplyTrustedCorrelationHeaders(
        HttpRequestMessage request, TaskTraceContext? trusted = null, bool fillIdentity = true)
    {
        request.Headers.Remove(WorkflowInstanceHeader);
        request.Headers.Remove(CorrelationHeader);

        var workflowInstance = trusted?.InstanceId is { } trustedId && trustedId != Guid.Empty
            ? trustedId.ToString("D")
            : Activity.Current?.GetBaggageItem(WorkflowInstanceBaggage);
        if (Guid.TryParse(workflowInstance, out var workflowInstanceId)
            && workflowInstanceId != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation(
                WorkflowInstanceHeader,
                workflowInstanceId.ToString("D").ToLowerInvariant());
        }

        var correlation = !string.IsNullOrEmpty(trusted?.CorrelationId)
            ? trusted.CorrelationId
            : Activity.Current?.GetBaggageItem(CorrelationBaggage);
        if (Guid.TryParseExact(correlation, "N", out var correlationId)
            && correlationId != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId.ToString("N"));
        }

        if (fillIdentity)
        {
            FillIdentityIfAbsent(request, SubHeader, trusted?.Sub, SubBaggage);
            FillIdentityIfAbsent(request, ActSubHeader, trusted?.ActSub, ActSubBaggage);
        }

        ApplyRequestId(request, trusted);
    }

    /// <summary>
    /// Adds an identity claim header only when the request does not already carry it — an empty-valued header counts
    /// as carried, which is how a blank credential marker stops the fill. The trusted context wins over ambient baggage.
    /// </summary>
    private static void FillIdentityIfAbsent(
        HttpRequestMessage request, string header, string? trustedValue, string baggageKey)
    {
        if (request.Headers.NonValidated.Contains(header))
            return;

        var value = !string.IsNullOrEmpty(trustedValue)
            ? trustedValue
            : Activity.Current?.GetBaggageItem(baggageKey);
        if (IsSafeIdentityClaim(value))
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }
    }

    /// <summary>
    /// Fills <c>X-Request-Id</c> on an outbound request when the task binding's input mapping left
    /// it absent or empty: a non-empty mapping value always wins; otherwise vNext's own request id
    /// is sent unchanged — <paramref name="trusted"/>'s <c>RequestId</c> when supplied, else the
    /// ambient <c>x_request_id</c> baggage the Execution host restores from the invoke envelope.
    /// Nothing is generated when neither has one. Part of <see cref="ApplyTrustedCorrelationHeaders"/>;
    /// public on its own for the invokers that apply ONLY this rule (StartTrigger and SubProcess,
    /// whose calls never carried the workflow-context or identity headers).
    /// </summary>
    public static void ApplyRequestId(HttpRequestMessage request, TaskTraceContext? trusted = null)
    {
        if (HasNonEmptyHeader(request, RequestIdHeader))
            return;

        var requestId = !string.IsNullOrEmpty(trusted?.RequestId)
            ? trusted.RequestId
            : Activity.Current?.GetBaggageItem(RequestIdBaggage);
        if (IsSafeHeaderValue(requestId))
        {
            // Remove first: an empty mapping entry would otherwise be sent next to the value.
            request.Headers.Remove(RequestIdHeader);
            request.Headers.TryAddWithoutValidation(RequestIdHeader, requestId);
        }
    }

    /// <summary>Whether the request already carries <paramref name="name"/> with a non-blank value.</summary>
    private static bool HasNonEmptyHeader(HttpRequestMessage request, string name) =>
        request.Headers.NonValidated.TryGetValues(name, out var values)
        && values.Any(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// The caller credential a task carries to its target: <c>sub</c>, <c>act_sub</c>, <c>position</c>, <c>client_id</c>
    /// and <c>role</c>. Only these are forwarded from the caller; everything else the target sees comes from the task's own
    /// header definition.
    /// </summary>
    private static readonly string[] CredentialHeaders = [SubHeader, ActSubHeader, PositionHeader, ClientIdHeader, RoleHeader];

    private const string PositionHeader = "position";
    private const string ClientIdHeader = "client_id";
    private const string RoleHeader = "role";

    /// <summary>
    /// The header set a task presents to its target, as a dictionary: the binding's headers without the reserved
    /// trace/correlation headers and without Content-Type, with the credential (<c>sub</c>, <c>act_sub</c>,
    /// <c>position</c>, <c>client_id</c>, <c>role</c>) resolved by <see cref="ResolveInstanceCallCredential"/>: a
    /// credential the binding set — blank or null included — is the binding's decision and stays as it is, an absent one
    /// is filled from <paramref name="callerHeaders"/>, the caller's own request headers (Activity baggage is not a
    /// source). <c>role</c> is forwarded only as the caller SENT it: roles a provider resolved for the caller (morph-idm
    /// get-roles) are not in these headers and are never carried — the target resolves them itself from the forwarded
    /// credential. A null-valued non-credential header is dropped. Used for a GetInstance / GetInstances /
    /// GetInstanceData read, in-process (as the read's current user) and cross-domain (as the request's headers) alike.
    /// Keys are case-insensitive.
    /// </summary>
    public static Dictionary<string, string?> BuildOutgoingHeaders(
        IReadOnlyDictionary<string, string?>? bindingHeaders,
        IReadOnlyDictionary<string, string>? callerHeaders = null)
    {
        var filtered = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (bindingHeaders is not null)
        {
            foreach (var (name, value) in bindingHeaders)
            {
                if (IsReservedTraceHeader(name) ||
                    string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;
                // A null credential is still "the binding set it" (blank); only a non-credential null is dropped.
                if (value is null && !IsCredentialHeader(name))
                    continue;
                filtered[name] = value;
            }
        }

        return ResolveInstanceCallCredential(filtered, callerHeaders);
    }

    /// <summary>
    /// Adds the caller's credential (<c>sub</c>, <c>act_sub</c>, <c>position</c>, <c>client_id</c>, <c>role</c>) to
    /// <paramref name="headers"/> for every credential header the task's mapping left absent or EMPTY — a non-empty mapping
    /// value always wins. Returns whether anything was added. Values come from <paramref name="callerHeaders"/>, the caller's request headers, so a role a provider
    /// resolved (morph-idm get-roles) is never carried; <c>sub</c> / <c>act_sub</c> fall back to Activity baggage. Nothing
    /// else is added or removed. <paramref name="headers"/> must use a case-insensitive comparer.
    /// </summary>
    public static bool AppendCallerCredential(
        IDictionary<string, string?> headers,
        IReadOnlyDictionary<string, string>? callerHeaders)
    {
        var changed = false;
        foreach (var name in CredentialHeaders)
        {
            if (headers.TryGetValue(name, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                continue;

            var value = CallerValue(callerHeaders, name);
            if (string.IsNullOrEmpty(value) && name == SubHeader)
                value = Activity.Current?.GetBaggageItem(SubBaggage);
            else if (string.IsNullOrEmpty(value) && name == ActSubHeader)
                value = Activity.Current?.GetBaggageItem(ActSubBaggage);

            var safe = name is SubHeader or ActSubHeader ? IsSafeIdentityClaim(value) : IsSafeHeaderValue(value);
            if (safe)
            {
                // Remove first: an empty mapping entry may be spelled with other casing than the canonical name.
                headers.Remove(name);
                headers[name] = value;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// The binding's header definition (JSON object) with the caller's credential added (see
    /// <see cref="AppendCallerCredential"/>). Every header the definition already carries is kept as is — Content-Type and
    /// reserved trace headers included, which the send path still handles — so only missing credential headers change.
    /// A definition that is not a JSON object of strings is returned unchanged.
    /// </summary>
    public static string? WithCallerCredential(string? headersJson, IReadOnlyDictionary<string, string>? callerHeaders)
    {
        Dictionary<string, string?> headers;
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            Dictionary<string, string?>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(headersJson);
            }
            catch (JsonException)
            {
                return headersJson;
            }

            if (parsed is null)
                return headersJson;
            headers = new Dictionary<string, string?>(parsed, StringComparer.OrdinalIgnoreCase);
        }

        return AppendCallerCredential(headers, callerHeaders) ? JsonSerializer.Serialize(headers) : headersJson;
    }

    /// <summary>
    /// The header set the runtime presents when it calls another instance: a SubFlow start, and the SubProcess, Start,
    /// DirectTrigger, GetInstance, GetInstances and GetInstanceData tasks — same-domain and cross-domain alike.
    /// <para>
    /// AUTHORIZATION RULE — the mapping decides, the caller is only the default:
    /// a credential header (<c>sub</c>, <c>act_sub</c>, <c>position</c>, <c>client_id</c>, <c>role</c>) the mapping SET,
    /// with any value, is the mapping's decision. A blank value (empty, whitespace or null) means "make this call
    /// WITHOUT this credential": the header is not sent and the caller's value is NOT filled in. A credential header the
    /// mapping did NOT set is filled from the caller's request headers.
    /// </para>
    /// <para>
    /// A flow author blanks a credential on purpose, to stop the caller's identity reaching the target. So "blank" must
    /// never be read as "not set" here — that would hand the target an identity the author withheld. Blank entries stay
    /// in the result as <c>""</c> and are dropped only at the exit (<c>CallerCredentialScope</c> in-process,
    /// <see cref="DropBlankCredentialHeaders"/> on a task's outbound request, the header merge of a cross-domain SubFlow
    /// start): the marker has to survive that far, because some exits fill a MISSING <c>sub</c> / <c>act_sub</c> themselves
    /// — the cross-domain SubFlow header merge from the ambient user, and an older Execution host that still stamps an
    /// absent <c>sub</c> / <c>act_sub</c> from the trace on a task's request.
    /// </para>
    /// <para>
    /// Activity baggage is deliberately not a source: a child whose parent blanked <c>sub</c> must not pick the first
    /// caller's <c>sub</c> back up from the trace on its own calls. HTTP, SOAP and Dapr tasks keep the older
    /// fill-when-blank rule (<see cref="AppendCallerCredential"/>). Full guide:
    /// docs/domain/role-grant-authorization.md#caller-credential-on-instance-calls.
    /// </para>
    /// </summary>
    public static Dictionary<string, string?> ResolveInstanceCallCredential(
        IReadOnlyDictionary<string, string?>? mappingHeaders,
        IReadOnlyDictionary<string, string>? callerHeaders)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (mappingHeaders is not null)
        {
            foreach (var (name, value) in mappingHeaders)
                result[name] = value;
        }

        foreach (var name in CredentialHeaders)
        {
            if (result.TryGetValue(name, out var mapped))
            {
                // The mapping set it: its decision stands. Re-key under the canonical name; blank becomes "".
                result.Remove(name);
                result[name] = string.IsNullOrWhiteSpace(mapped) ? string.Empty : mapped;
                continue;
            }

            var value = CallerValue(callerHeaders, name);
            var safe = name is SubHeader or ActSubHeader ? IsSafeIdentityClaim(value) : IsSafeHeaderValue(value);
            if (safe)
                result[name] = value;
        }

        return result;
    }

    /// <summary>
    /// The binding's header definition (JSON object) resolved by <see cref="ResolveInstanceCallCredential"/>. Blank
    /// credentials stay in the JSON as <c>""</c> markers so a later exit can honour them. The definition is read by
    /// <see cref="ReadHeaderObject"/>, so a non-string value does not discard the set; text that does not parse, or is not
    /// a JSON object, is returned unchanged; an empty result is <c>null</c>.
    /// </summary>
    public static string? ResolveInstanceCallCredentialJson(string? headersJson, IReadOnlyDictionary<string, string>? callerHeaders)
    {
        Dictionary<string, string?>? parsed = null;
        if (!string.IsNullOrWhiteSpace(headersJson))
        {
            try
            {
                using var document = JsonDocument.Parse(headersJson);
                parsed = ReadHeaderObject(document.RootElement);
            }
            catch (JsonException)
            {
                return headersJson;
            }

            if (parsed is null)
                return headersJson;
        }

        var resolved = ResolveInstanceCallCredential(parsed, callerHeaders);
        return resolved.Count == 0 ? null : JsonSerializer.Serialize(resolved);
    }

    /// <summary>
    /// Reads a header definition (a JSON object) as a case-insensitive dictionary without discarding it over one
    /// non-string value: a string is its value, <c>null</c> stays null, a number / <c>true</c> / <c>false</c> is its JSON
    /// text, and a nested object or array becomes null. Returns <c>null</c> when <paramref name="element"/> is not a JSON
    /// object. For the instance-call tasks this is fail-closed: a credential key whose value is an object or array reads as
    /// null, i.e. blank, so the caller's value is withheld (<see cref="ResolveInstanceCallCredential"/>), never refilled.
    /// </summary>
    public static Dictionary<string, string?>? ReadHeaderObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                _ => null,
            };
        }

        return result;
    }

    /// <summary>Removes the credential keys whose value is blank (empty, whitespace or null); nothing else is touched.</summary>
    public static void RemoveBlankCredentials(IDictionary<string, string?> headers)
    {
        foreach (var name in CredentialHeaders)
        {
            if (headers.TryGetValue(name, out var value) && string.IsNullOrWhiteSpace(value))
                headers.Remove(name);
        }
    }

    /// <summary>Removes the credential headers whose value is blank from <paramref name="request"/>; nothing else is touched.</summary>
    public static void DropBlankCredentialHeaders(HttpRequestMessage request)
    {
        foreach (var name in CredentialHeaders.Where(
                     name => request.Headers.Contains(name) && !HasNonEmptyHeader(request, name)))
        {
            request.Headers.Remove(name);
        }
    }

    private static bool IsCredentialHeader(string name) =>
        CredentialHeaders.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

    private static string? CallerValue(IReadOnlyDictionary<string, string>? callerHeaders, string name)
    {
        if (callerHeaders is null)
            return null;
        foreach (var (key, value) in callerHeaders)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    private static (HttpClient Client, HttpRequestMessage Request) PrepareRequest(
        Func<string, HttpClient> createClient,
        HttpTaskBinding binding,
        string taskType,
        TaskTraceContext? traceContext,
        string? taskKey)
    {
        using (StartPrepareActivity(taskType, taskKey))
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

                        if (IsReservedTraceHeader(header.Key))
                            continue;

                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            ApplyTrustedCorrelationHeaders(request, traceContext);

            // Add body for non-GET requests. Resolution: explicit ContentType → Content-Type header → json.
            if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(binding.Body))
            {
                var contentType = HttpContentType.Resolve(binding.ContentType, headerContentType);
                request.Content = new StringContent(binding.Body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }

            return (httpClient, request);
        }
    }

    /// <summary>A header value that can be carried as is: present, bounded, no control characters (no header injection).</summary>
    private static bool IsSafeHeaderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            return false;
        return value.All(character => !char.IsControl(character));
    }

    private static bool IsSafeIdentityClaim(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

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
