using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins <see cref="DuplicateTolerantTraceContextPropagator"/>'s extraction contract against the
/// exact malformed <c>traceparent</c> Dapr's gRPC proxy-mode AppCallback hop was observed to send
/// (see <c>docs/runtime/dapr-invocation-transport.md</c>), plus the surrounding cases that decide
/// whether installing it process-wide is safe.
/// </summary>
public sealed class DuplicateTolerantTraceContextPropagatorTests
{
    /// <summary>
    /// The real header captured from a live gRPC proxy-mode invocation: one trace id, two span ids,
    /// comma-joined into a single value. Deliberately NOT a synthetic value.
    /// </summary>
    private const string CapturedDuplicatedTraceParent =
        "00-1c00cbe047937c981316a9a85f69bad6-52e645eaa63e82cb-01,00-1c00cbe047937c981316a9a85f69bad6-5c6c3f4a64d19975-01";

    private const string CapturedFirstValue = "00-1c00cbe047937c981316a9a85f69bad6-52e645eaa63e82cb-01";
    private const string CapturedLastValue = "00-1c00cbe047937c981316a9a85f69bad6-5c6c3f4a64d19975-01";
    private const string CapturedTraceId = "1c00cbe047937c981316a9a85f69bad6";

    private static readonly DistributedContextPropagator Default =
        DistributedContextPropagator.CreateDefaultPropagator();

    private static readonly DuplicateTolerantTraceContextPropagator Subject =
        DuplicateTolerantTraceContextPropagator.CreateOverDefault();

    [Fact]
    public void Duplicated_same_trace_id_collapses_to_one_parseable_context()
    {
        Subject.ExtractTraceIdAndState(
            Carrier.Single(CapturedDuplicatedTraceParent),
            Carrier.Getter,
            out var traceId,
            out var traceState);

        // The whole point: the trace id survives, so the app's server span joins the caller's tree.
        traceId.ShouldNotBeNull();
        traceId.ShouldContain(CapturedTraceId);

        ActivityContext.TryParse(traceId, traceState, isRemote: true, out var context).ShouldBeTrue();
        context.TraceId.ToHexString().ShouldBe(CapturedTraceId);
        context.SpanId.ToHexString().ShouldNotBe("0000000000000000");

        // The default propagator, given the same raw value, cannot interpret it at all -- which is
        // exactly why ASP.NET Core roots a fresh trace without this decorator.
        Default.ExtractTraceIdAndState(
            Carrier.Single(CapturedDuplicatedTraceParent),
            Carrier.Getter,
            out var defaultTraceId,
            out var defaultTraceState);
        ActivityContext.TryParse(defaultTraceId, defaultTraceState, isRemote: true, out _).ShouldBeFalse();
    }

    [Fact]
    public void Duplicated_same_trace_id_keeps_the_last_span_id()
    {
        // Documents the winning-span-id decision. Identified against the live trace this header
        // came from: 52e645eaa63e82cb (first) is the app's own System.Net.Http client span,
        // 5c6c3f4a64d19975 (last) is the CALLER sidecar's dapr-diagnostics span -- the deepest
        // node the header actually offers. See the propagator's own comment for the full chain.
        Subject.ExtractTraceIdAndState(
            Carrier.Single(CapturedDuplicatedTraceParent),
            Carrier.Getter,
            out var traceId,
            out var traceState);

        ActivityContext.TryParse(traceId, traceState, isRemote: true, out var context).ShouldBeTrue();
        context.SpanId.ToHexString().ShouldBe("5c6c3f4a64d19975");
        context.SpanId.ToHexString().ShouldNotBe("52e645eaa63e82cb");
    }

    [Fact]
    public void Duplication_delivered_as_two_separate_values_gives_the_same_result()
    {
        Subject.ExtractTraceIdAndState(
            Carrier.Multi(CapturedFirstValue, CapturedLastValue),
            Carrier.Getter,
            out var multiTraceId,
            out var multiTraceState);

        Subject.ExtractTraceIdAndState(
            Carrier.Single(CapturedDuplicatedTraceParent),
            Carrier.Getter,
            out var joinedTraceId,
            out var joinedTraceState);

        multiTraceId.ShouldBe(joinedTraceId);
        multiTraceState.ShouldBe(joinedTraceState);

        ActivityContext.TryParse(multiTraceId, multiTraceState, isRemote: true, out var context).ShouldBeTrue();
        context.TraceId.ToHexString().ShouldBe(CapturedTraceId);
    }

    [Theory]
    [InlineData(CapturedFirstValue, null)]
    [InlineData(CapturedLastValue, "congo=t61rcWkgMzE")]
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-00", "rojo=00f067aa0ba902b7")]
    public void Single_well_formed_value_is_delegated_untouched(string traceParent, string? traceState)
    {
        var carrier = Carrier.Single(traceParent, traceState);

        Subject.ExtractTraceIdAndState(carrier, Carrier.Getter, out var actualId, out var actualState);
        Default.ExtractTraceIdAndState(carrier, Carrier.Getter, out var expectedId, out var expectedState);

        actualId.ShouldBe(expectedId);
        actualState.ShouldBe(expectedState);
        actualId.ShouldBe(traceParent);
    }

    [Fact]
    public void Values_with_differing_trace_ids_are_treated_as_absent()
    {
        const string differentTrace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        foreach (var carrier in new[]
                 {
                     Carrier.Single($"{CapturedFirstValue},{differentTrace}"),
                     Carrier.Multi(CapturedFirstValue, differentTrace)
                 })
        {
            Subject.ExtractTraceIdAndState(carrier, Carrier.Getter, out var traceId, out var traceState);

            traceId.ShouldBeNull();
            ActivityContext.TryParse(traceId, traceState, isRemote: true, out _).ShouldBeFalse();
        }
    }

    [Theory]
    // Garbage in the duplicated shape -> absent, never a guess.
    [InlineData("not-a-traceparent,also-not-one")]
    [InlineData(CapturedFirstValue + ",garbage")]
    [InlineData("garbage," + CapturedFirstValue)]
    // Structurally invalid duplicates: all-zero trace id, all-zero span id, forbidden version,
    // wrong field lengths, uppercase hex.
    [InlineData("00-00000000000000000000000000000000-52e645eaa63e82cb-01,00-00000000000000000000000000000000-5c6c3f4a64d19975-01")]
    [InlineData("00-1c00cbe047937c981316a9a85f69bad6-0000000000000000-01," + CapturedLastValue)]
    [InlineData("ff-1c00cbe047937c981316a9a85f69bad6-52e645eaa63e82cb-01," + CapturedLastValue)]
    [InlineData("00-1c00cbe047937c981316a9a85f69bad6-52e645eaa63e82cb-01-extra," + CapturedLastValue)]
    [InlineData("00-1C00CBE047937C981316A9A85F69BAD6-52E645EAA63E82CB-01," + CapturedLastValue)]
    [InlineData(",,,")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_input_is_treated_as_absent_without_throwing(string? rawHeader)
    {
        Subject.ExtractTraceIdAndState(
            Carrier.Single(rawHeader),
            Carrier.Getter,
            out var traceId,
            out var traceState);

        ActivityContext.TryParse(traceId, traceState, isRemote: true, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_throwing_getter_does_not_escape_as_an_exception()
    {
        // A propagator that throws breaks every request that flows through it.
        static void ThrowingGetter(
            object? carrier,
            string fieldName,
            out string? fieldValue,
            out IEnumerable<string>? fieldValues) =>
            throw new InvalidOperationException("carrier exploded");

        Subject.ExtractTraceIdAndState(
            Carrier.Single(CapturedDuplicatedTraceParent),
            ThrowingGetter,
            out var traceId,
            out var traceState);

        traceId.ShouldBeNull();
        traceState.ShouldBeNull();
    }

    [Fact]
    public void Null_getter_is_delegated()
    {
        Should.NotThrow(() => Subject.ExtractTraceIdAndState(null, null, out _, out _));
    }

    [Fact]
    public void Fields_and_baggage_are_delegated_unchanged()
    {
        Subject.Fields.ShouldBe(Default.Fields);

        var carrier = Carrier.Single(CapturedFirstValue);
        carrier.Headers["baggage"] = ["k1=v1,k2=v2"];
        carrier.Headers["Correlation-Context"] = ["k1=v1,k2=v2"];

        var actual = Subject.ExtractBaggage(carrier, Carrier.Getter)?.ToList();
        var expected = Default.ExtractBaggage(carrier, Carrier.Getter)?.ToList();

        if (expected is null)
        {
            actual.ShouldBeNull();
            return;
        }

        actual.ShouldNotBeNull();
        actual.ShouldBe(expected);
    }

    [Fact]
    public void Inject_produces_exactly_what_the_default_propagator_produces()
    {
        using var source = new ActivitySource(nameof(DuplicateTolerantTraceContextPropagatorTests));
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("inject-parity");
        activity.ShouldNotBeNull();
        activity.TraceStateString = "congo=t61rcWkgMzE";
        activity.AddBaggage("k1", "v1");

        var actual = new Dictionary<string, string?>();
        var expected = new Dictionary<string, string?>();

        Subject.Inject(activity, actual, (carrier, key, value) => ((Dictionary<string, string?>)carrier!)[key] = value);
        Default.Inject(activity, expected, (carrier, key, value) => ((Dictionary<string, string?>)carrier!)[key] = value);

        actual.ShouldBe(expected, ignoreOrder: true);
        actual.ShouldContainKey("traceparent");
        actual["traceparent"].ShouldBe(activity.Id);
    }

    /// <summary>
    /// Minimal stand-in for an HTTP header collection, exercising both carrier shapes the
    /// <see cref="DistributedContextPropagator.PropagatorGetterCallback"/> contract allows:
    /// a single <c>string</c> value, or an <c>IEnumerable&lt;string&gt;</c> of values.
    /// </summary>
    private sealed class Carrier
    {
        public Dictionary<string, string?[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Single-value shape: sets <c>fieldValue</c>, leaves <c>fieldValues</c> null.</summary>
        public static Carrier Single(string? traceParent, string? traceState = null)
        {
            var carrier = new Carrier { Headers = { ["traceparent"] = [traceParent] } };
            if (traceState is not null)
            {
                carrier.Headers["tracestate"] = [traceState];
            }

            return carrier;
        }

        /// <summary>Multi-value shape: sets <c>fieldValues</c>, leaves <c>fieldValue</c> null.</summary>
        public static Carrier Multi(params string?[] traceParents) =>
            new() { Headers = { ["traceparent"] = traceParents } };

        /// <summary>
        /// Mirrors ASP.NET Core's own header getter: one value goes out as <c>fieldValue</c>,
        /// several as <c>fieldValues</c>.
        /// </summary>
        public static void Getter(
            object? carrier,
            string fieldName,
            out string? fieldValue,
            out IEnumerable<string>? fieldValues)
        {
            fieldValue = null;
            fieldValues = null;

            if (carrier is not Carrier typed || !typed.Headers.TryGetValue(fieldName, out var values))
            {
                return;
            }

            if (values.Length <= 1)
            {
                fieldValue = values.Length == 1 ? values[0] : null;
                return;
            }

            fieldValues = values.Select(value => value!);
        }
    }
}
