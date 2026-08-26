using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BBT.Workflow.HttpApi.Shared.Telemetry;

/// <summary>
/// A <see cref="DistributedContextPropagator"/> decorator that repairs the <c>traceparent</c>
/// header before the runtime interprets it, so a request whose <c>traceparent</c> arrives
/// <b>duplicated</b> still joins the caller's trace instead of rooting a brand-new one.
/// Extraction is the only thing corrected — <see cref="Fields"/>, <see cref="Inject"/> and
/// <see cref="ExtractBaggage"/> are delegated to the inner propagator verbatim.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this works around is Dapr's, not ours.</b> When the orchestration → execution hop
/// runs over Dapr gRPC proxy mode (<c>ExecutionApi:Transport = grpc</c>), the callee sidecar's
/// app-bound AppCallback hop re-issues its own gRPC call to the app rather than proxying the
/// original HTTP/2 stream, and it <i>appends</i> its own span to the incoming <c>traceparent</c>
/// instead of replacing it. The app therefore receives two comma-joined values:
/// </para>
/// <code>
/// 00-1c00cbe047937c981316a9a85f69bad6-52e645eaa63e82cb-01,00-1c00cbe047937c981316a9a85f69bad6-5c6c3f4a64d19975-01
/// </code>
/// <para>
/// Same trace id, two different span ids. More than one <c>traceparent</c> is invalid per the W3C
/// Trace Context spec, and a compliant receiver MUST treat it as if no trace context was present —
/// which is exactly what ASP.NET Core's hosting instrumentation does. The result is a fresh root
/// <see cref="Activity"/> and one logical call split across two disconnected traces.
/// </para>
/// <para>
/// <b>Why a propagator is the only viable seam.</b> ASP.NET Core's hosting layer builds the
/// incoming request's <see cref="Activity"/> from <see cref="DistributedContextPropagator.Current"/>
/// <i>before</i> any application code runs, and <see cref="Activity.ParentId"/> is immutable once
/// started. No middleware, filter, or body-based fallback can re-parent it afterwards; a propagator
/// runs strictly earlier, so this is the only place the malformed value can still be corrected.
/// </para>
/// <para>Full investigation, evidence and verification: <c>docs/runtime/dapr-invocation-transport.md</c>.</para>
/// </remarks>
public sealed class DuplicateTolerantTraceContextPropagator : DistributedContextPropagator
{
    private const string TraceParentFieldName = "traceparent";

    /// <summary>Length of the trace-id field of a <c>traceparent</c>, in hex characters.</summary>
    private const int TraceIdLength = 32;

    /// <summary>Length of the parent-id (span-id) field of a <c>traceparent</c>, in hex characters.</summary>
    private const int SpanIdLength = 16;

    /// <summary>Length of the version and trace-flags fields of a <c>traceparent</c>, in hex characters.</summary>
    private const int ByteFieldLength = 2;

    /// <summary>
    /// Name of the <see cref="System.Diagnostics.Metrics.Meter"/> this type publishes on. Must be
    /// listed in the host's <c>Telemetry:Metrics:AdditionalMeters</c> for the counter below to be
    /// exported (Aether feeds that list straight into <c>metrics.AddMeter</c>).
    /// </summary>
    public const string MeterName = "BBT.Workflow.Telemetry";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    /// Counts only the requests where this decorator actually changed the outcome, tagged by
    /// <c>outcome</c>. Deliberately NOT incremented on the untouched path, so the metric stays
    /// silent (and free) for well-formed traffic and is alertable in both directions:
    /// <list type="bullet">
    /// <item><c>repaired</c> — a duplicated header was collapsed. Sustained <c>0</c> while gRPC
    /// proxy mode is enabled means Dapr fixed its AppCallback hop and this decorator is now dead
    /// code that can be removed.</item>
    /// <item><c>trace_id_mismatch</c> / <c>unparseable</c> — the malformation changed shape. Both
    /// degrade to rooting a new trace, i.e. the original bug returns, so any non-zero value here
    /// is the signal that this workaround has stopped covering reality.</item>
    /// <item><c>error</c> — the carrier or inner propagator threw and extraction was degraded to
    /// "no incoming trace context".</item>
    /// </list>
    /// Without this, "Dapr fixed it upstream" and "the malformation changed shape" are
    /// indistinguishable — both look like silence.
    /// </summary>
    private static readonly Counter<long> ExtractionOutcomes = Meter.CreateCounter<long>(
        "workflow_traceparent_extractions_total",
        unit: "extractions",
        description: "traceparent extractions where the duplicate-tolerant propagator changed the outcome, by outcome.");

    private static readonly KeyValuePair<string, object?> OutcomeRepaired = new("outcome", "repaired");
    private static readonly KeyValuePair<string, object?> OutcomeTraceIdMismatch = new("outcome", "trace_id_mismatch");
    private static readonly KeyValuePair<string, object?> OutcomeUnparseable = new("outcome", "unparseable");
    private static readonly KeyValuePair<string, object?> OutcomeError = new("outcome", "error");

    private readonly DistributedContextPropagator _inner;

    /// <summary>
    /// Creates a decorator over <paramref name="inner"/>. Pass the propagator that would otherwise
    /// have been in force — typically <see cref="DistributedContextPropagator.Current"/> or
    /// <see cref="DistributedContextPropagator.CreateDefaultPropagator"/>.
    /// </summary>
    /// <param name="inner">The propagator that does the actual parsing, injection and baggage work.</param>
    public DuplicateTolerantTraceContextPropagator(DistributedContextPropagator inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    /// Creates a decorator over <see cref="DistributedContextPropagator.CreateDefaultPropagator"/>,
    /// i.e. the propagator the runtime would have used had this type not been installed.
    /// </summary>
    public static DuplicateTolerantTraceContextPropagator CreateOverDefault() =>
        new(CreateDefaultPropagator());

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Fields => _inner.Fields;

    /// <inheritdoc />
    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        _inner.Inject(activity, carrier, setter);

    /// <inheritdoc />
    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier,
        PropagatorGetterCallback? getter) =>
        _inner.ExtractBaggage(carrier, getter);

    /// <summary>
    /// Extracts the trace id and trace state, normalizing a duplicated <c>traceparent</c> down to a
    /// single value first. A well-formed single value is handed to the inner propagator untouched.
    /// </summary>
    public override void ExtractTraceIdAndState(
        object? carrier,
        PropagatorGetterCallback? getter,
        out string? traceId,
        out string? traceState)
    {
        // The getter callback is wrapped rather than the result post-processed, so that all actual
        // parsing stays the inner propagator's job: we only decide which single string it gets to
        // see for "traceparent". Every other field the inner propagator asks for -- "tracestate"
        // for the W3C propagator that is the .NET default, "Request-Id" for the legacy one --
        // passes through byte-for-byte, and we neither add nor suppress a lookup.
        void NormalizingGetter(
            object? innerCarrier,
            string fieldName,
            out string? fieldValue,
            out IEnumerable<string>? fieldValues)
        {
            getter(innerCarrier, fieldName, out fieldValue, out fieldValues);

            if (!string.Equals(fieldName, TraceParentFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!TryNormalizeTraceParent(fieldValue, fieldValues, out var normalized))
            {
                // Untouched: the input needed no correction. Guaranteed for any value delivered
                // as a single string containing no comma -- i.e. every well-formed traceparent,
                // since a valid one never contains a comma. That is the reason installing this
                // propagator process-wide is safe.
                return;
            }

            // normalized == null means "malformed beyond repair" -> report the field as absent,
            // per W3C: an uninterpretable traceparent MUST be treated as if not present.
            fieldValue = normalized;
            fieldValues = null;
        }

        try
        {
            // A null getter means there is nothing to read and therefore nothing to normalize;
            // hand it straight to the inner propagator. Kept INSIDE the try so that every path
            // out of this method -- including the inner propagator's own handling of a null
            // getter -- is covered by the catch below, rather than one narrow path being exempt.
            _inner.ExtractTraceIdAndState(
                carrier,
                getter is null ? null : NormalizingGetter,
                out traceId,
                out traceState);
        }
        catch (Exception)
        {
            // A propagator that throws breaks every request that flows through it. Degrade to
            // "no incoming trace context" instead, which the runtime already knows how to handle.
            ExtractionOutcomes.Add(1, OutcomeError);
            traceId = null;
            traceState = null;
        }
    }

    /// <summary>
    /// Decides what single <c>traceparent</c> value, if any, the inner propagator should see.
    /// </summary>
    /// <param name="fieldValue">The single-value form the carrier's getter produced, if any.</param>
    /// <param name="fieldValues">The multi-value form the carrier's getter produced, if any.</param>
    /// <param name="normalized">
    /// The value to substitute, or <see langword="null"/> to report the field as absent. Only
    /// meaningful when this method returns <see langword="true"/>.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the input needs no correction and must be delegated untouched;
    /// <see langword="true"/> when <paramref name="normalized"/> should replace it.
    /// </returns>
    private static bool TryNormalizeTraceParent(
        string? fieldValue,
        IEnumerable<string>? fieldValues,
        out string? normalized)
    {
        normalized = null;

        // FAST PATH -- the overwhelmingly common shape: one header value, delivered as a single
        // string, containing no comma. A well-formed traceparent never contains a comma, so this
        // covers every normal request, and returning here allocates nothing at all: no List, no
        // string[], no enumerator. That makes "this decorator is invisible on well-formed traffic"
        // a property of the code rather than a claim in a comment -- which matters because the
        // propagator is process-global and sits in front of EVERY inbound request.
        if (fieldValues is null && fieldValue is not null && !fieldValue.Contains(','))
        {
            return false;
        }

        // Duplication reaches us in either carrier shape: as one comma-joined string (what Dapr's
        // AppCallback hop actually produces today, because the duplicate is appended into a single
        // header value) or as two separate header values (what a strictly-conforming HTTP stack
        // would produce for a repeated header). Both are handled by flattening then splitting.
        List<string> parts = [];

        if (fieldValues is not null)
        {
            foreach (var value in fieldValues)
            {
                AppendParts(value, parts);
            }
        }

        // fieldValue is consulted ONLY when the multi-value shape produced nothing, so a getter
        // that populates both shapes cannot make us count the same header twice.
        if (parts.Count == 0)
        {
            if (fieldValue is null)
            {
                return false; // Absent already — nothing to correct.
            }

            AppendParts(fieldValue, parts);
        }

        if (parts.Count == 0)
        {
            // Present but empty / whitespace / only separators. Absent as far as the spec cares,
            // and the inner propagator treats empty the same way, so delegate untouched.
            return false;
        }

        if (parts.Count == 1)
        {
            // One logical value that arrived in the multi-value shape, or as a single string that
            // carried a stray comma or surrounding whitespace. (A clean single string never gets
            // here — the fast path above already returned it untouched.) Hand over the canonical
            // string; the inner propagator still owns deciding whether it is actually valid.
            //
            // Deliberately NOT counted as "repaired": this is not the Dapr duplication, and
            // counting it would break the "repaired == 0 ⇒ the workaround is dead code" reading
            // of the metric.
            normalized = parts[0];
            return true;
        }

        // More than one value: the malformed case. Every part must be a parseable traceparent AND
        // they must all agree on the trace id; anything else is uninterpretable and is reported as
        // absent rather than guessed at.
        string? agreedTraceId = null;
        foreach (var part in parts)
        {
            if (!TryGetTraceId(part, out var partTraceId))
            {
                ExtractionOutcomes.Add(1, OutcomeUnparseable);
                return true; // normalized stays null -> absent.
            }

            if (agreedTraceId is null)
            {
                agreedTraceId = partTraceId;
            }
            else if (!string.Equals(agreedTraceId, partTraceId, StringComparison.OrdinalIgnoreCase))
            {
                // Different traces. Per W3C we must not pick a winner between unrelated traces.
                ExtractionOutcomes.Add(1, OutcomeTraceIdMismatch);
                return true; // absent.
            }
        }

        // All parts share the trace id, so collapsing to any one of them keeps the trace whole —
        // which is the property that actually matters. The span-id choice only decides which node
        // the app's server span hangs under.
        //
        // We take the LAST value, and that is not a coin flip — the two duplicated span ids were
        // identified against a real gRPC-proxy-mode trace in Elastic
        // (trace 1c00cbe047937c981316a9a85f69bad6, the very value the tests use):
        //
        //   Task.Invoke                                                       0904b2437acd8f3c
        //   └─ bbt.workflow.execution.v1.TaskInvoker/Invoke  (GrpcNetClient)  bfa61e1b2e7cd43a
        //      └─ POST                                       (System.Net.Http) 52e645eaa63e82cb  <- FIRST value
        //         └─ /...TaskInvoker/Invoke   (dapr-diagnostics, CALLER sidecar) 5c6c3f4a64d19975  <- LAST value
        //            └─ /...TaskInvoker/Invoke (dapr-diagnostics, CALLEE sidecar) 366bc7bc14789e4d
        //
        // So the first value is the app's own outbound HTTP client span and the last is the caller
        // sidecar's span. The callee sidecar's own span (366bc7bc…) is in NEITHER value — the
        // AppCallback hop appends the context it received rather than the one it created — so
        // "hang under the callee sidecar" is simply not reachable from this header. The last value
        // is the deepest node that IS offered, making the app's server span a sibling of the callee
        // sidecar transaction instead of a grandchild-skipping child of the HTTP client span, which
        // is what taking the first value would produce. This also matches how HTTP header lists
        // behave generally: values are appended, so the hop nearest the app wrote last.
        normalized = parts[^1];
        ExtractionOutcomes.Add(1, OutcomeRepaired);
        return true;
    }

    /// <summary>
    /// Splits <paramref name="value"/> on commas, appending each non-empty trimmed segment to
    /// <paramref name="parts"/>.
    /// </summary>
    private static void AppendParts(string value, List<string> parts) =>
        parts.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Parses the trace-id out of a single <c>traceparent</c> value, validating the structural
    /// rules the W3C spec makes mandatory (field lengths, hex-only, non-zero trace/parent ids).
    /// </summary>
    private static bool TryGetTraceId(string traceParent, out string traceId)
    {
        traceId = string.Empty;

        var fields = traceParent.Split('-');
        if (fields.Length < 4)
        {
            return false;
        }

        var version = fields[0];
        var candidateTraceId = fields[1];
        var parentId = fields[2];
        var traceFlags = fields[3];

        if (version.Length != ByteFieldLength ||
            candidateTraceId.Length != TraceIdLength ||
            parentId.Length != SpanIdLength ||
            traceFlags.Length != ByteFieldLength)
        {
            return false;
        }

        // Version 00 is exactly four fields; later versions may append more, which a 00 parser is
        // required to tolerate on those versions only. "ff" is explicitly forbidden.
        if (string.Equals(version, "ff", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(version, "00", StringComparison.Ordinal) && fields.Length != 4))
        {
            return false;
        }

        if (!IsLowerHex(version) || !IsLowerHex(candidateTraceId) ||
            !IsLowerHex(parentId) || !IsLowerHex(traceFlags))
        {
            return false;
        }

        if (IsAllZeros(candidateTraceId) || IsAllZeros(parentId))
        {
            return false;
        }

        traceId = candidateTraceId;
        return true;
    }

    // The spec mandates lowercase hex; accepting uppercase here would let a value through that the
    // inner propagator would then reject, turning a "treat as absent" decision into a broken one.
    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllZeros(string value)
    {
        foreach (var c in value)
        {
            if (c != '0')
            {
                return false;
            }
        }

        return true;
    }
}
