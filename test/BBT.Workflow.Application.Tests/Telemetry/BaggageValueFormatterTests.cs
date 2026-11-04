using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins <see cref="BaggageValueFormatter"/>: a flow version with SemVer build metadata must reach a
/// legacy-propagator .NET 8 service intact instead of turning its <c>+</c> into a space, which Dapr
/// then rejects as <c>invalid baggage header</c>.
/// </summary>
public sealed class BaggageValueFormatterTests
{
    private const string Key = "vnext.flow.version";

    [Theory]
    [InlineData("1.0.0-pkg.1.0.346+contract", "1.0.0-pkg.1.0.346_contract")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0-rc.1+build.5", "1.0.0-rc.1_build.5")]
    [InlineData("a b,c;d=e%f/g", "a_b_c_d_e_f_g")]
    [InlineData("~keep-._", "~keep-._")]
    [InlineData("1.0.0+ğ", "1.0.0__")]
    public void Format_ReplacesEveryNonUnreservedCharacter(string input, string expected)
    {
        BaggageValueFormatter.Format(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Format_PassesNullAndEmptyThrough(string? input)
    {
        BaggageValueFormatter.Format(input).ShouldBe(input);
    }

    [Fact]
    public void Format_ReturnsSameInstance_WhenAlreadySafe()
    {
        const string value = "1.0.0-pkg.1.0.346";
        BaggageValueFormatter.Format(value).ShouldBeSameAs(value);
    }

    /// <summary>
    /// The failing chain end to end: vNext (W3C) → a W3C service that re-injects → a legacy service.
    /// The raw <c>+</c> arrives as a space at both legacy readers; the formatted value arrives intact.
    /// </summary>
    [Theory]
    [InlineData("1.0.0-pkg.1.0.346+contract")]
    [InlineData("1.0.0-rc.1+build.5")]
    public void FormattedVersion_SurvivesLegacyAndSecondHop(string version)
    {
        var w3c = DistributedContextPropagator.CreateW3CPropagator();
        var legacy = DistributedContextPropagator.CreatePreW3CPropagator();
        var formatted = BaggageValueFormatter.Format(version)!;

        var fromVnext = Inject(w3c, formatted);
        Extract(legacy, fromVnext).ShouldBe(formatted);

        var w3cRead = Extract(w3c, fromVnext);
        w3cRead.ShouldBe(formatted);
        Extract(legacy, Inject(w3c, w3cRead)).ShouldBe(formatted);

        // Contrast: the raw value is what breaks today.
        Extract(legacy, Inject(w3c, version)).ShouldContain(" ");
    }

    private static Dictionary<string, string> Inject(DistributedContextPropagator propagator, string value)
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            using var activity = new Activity("baggage-format").SetIdFormat(ActivityIdFormat.W3C).Start();
            activity.SetBaggage(Key, value);
            var headers = new Dictionary<string, string>();
            propagator.Inject(activity, headers, static (carrier, name, headerValue) =>
                ((Dictionary<string, string>)carrier!)[name] = headerValue);
            return headers;
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    private static string Extract(DistributedContextPropagator propagator, Dictionary<string, string> headers)
    {
        var baggage = propagator.ExtractBaggage(headers,
            static (object? carrier, string name, out string? fieldValue, out IEnumerable<string>? fieldValues) =>
            {
                fieldValues = null;
                ((Dictionary<string, string>)carrier!).TryGetValue(name, out fieldValue);
            });
        baggage.ShouldNotBeNull();
        return baggage.Single(item => item.Key == Key).Value!;
    }
}
