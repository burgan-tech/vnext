using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Guards the read-envelope contract that makes per-function latency obtainable at all.
/// <para>
/// Built-in and custom functions share one route template, so Elastic's <c>transaction.name</c> is
/// identical for a state poll, a view read and a custom-function call. The envelope is the only
/// thing carrying the distinction — which means a read entry point that silently ships without one
/// is invisible again, and nothing else in the suite would notice.
/// </para>
/// <para>
/// Deliberately a source check rather than a behavioural one: wiring a fake for each of the thirteen
/// entry points would cost more than the spans it guards and would still miss the fourteenth. What
/// actually fails here is "somebody added a public read and forgot the envelope".
/// </para>
/// </summary>
public sealed class InstanceReadEnvelopeTests
{
    /// <summary>
    /// Empty on purpose. State and data were sequenced behind the effective-status-fingerprint
    /// change while it was unmerged; it has since landed (#983) and both now open an envelope — on
    /// their BUILD branch only. The 304 / cache-hit branch creates nothing, which
    /// <c>GetInstanceStateAsync_304Branch_CreatesNoSpansAndTagsTheTransaction</c> enforces as a
    /// number rather than a convention.
    /// </summary>
    private static readonly string[] DeferredEntryPoints = [];

    [Fact]
    public void EveryPublicRead_OpensAnEnvelope()
    {
        var source = ReadQueryServiceSource();

        var missing = PublicReadMethods()
            .Where(method => !DeferredEntryPoints.Contains(method))
            .Where(method => !OpensEnvelope(source, method))
            .ToList();

        missing.ShouldBeEmpty(
            $"these read entry points open no Instance.Read envelope: {string.Join(", ", missing)}. " +
            "Without one the read is indistinguishable from every other function call on the shared " +
            "route template.");
    }

    /// <summary>
    /// The kind sits in the span NAME, so the set has to stay closed. An interpolated or computed
    /// value here would mint a span name per instance and break aggregation for every reader.
    /// </summary>
    [Fact]
    public void EveryEnvelope_UsesAConstantKind()
    {
        var source = ReadQueryServiceSource();

        var calls = Regex.Matches(source, @"StartRead\(\s*(?<arg>[^,\)]+)")
            .Select(match => match.Groups["arg"].Value.Trim())
            .ToList();

        calls.ShouldNotBeEmpty();
        calls.ShouldAllBe(argument => argument.StartsWith("InstanceReadKinds.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The kinds that also name a subflow descent must use the same strings, or a reader correlating
    /// an envelope with the descent ladder underneath it has to translate between two vocabularies
    /// for the same function.
    /// </summary>
    [Theory]
    [InlineData(InstanceReadKinds.State, "state")]
    [InlineData(InstanceReadKinds.View, "view")]
    [InlineData(InstanceReadKinds.Schema, "schema")]
    [InlineData(InstanceReadKinds.Master, "master")]
    [InlineData(InstanceReadKinds.Extensions, "extensions")]
    public void ReadKinds_MatchTheDescentVocabulary(string readKind, string descentFunction)
    {
        readKind.ShouldBe(descentFunction);
    }

    private static bool OpensEnvelope(string source, string methodName)
    {
        var start = source.IndexOf($" {methodName}(", StringComparison.Ordinal);
        if (start < 0) return false;

        // Window = this method's body, bounded by the next public entry point rather than by a fixed
        // character count. The state and data methods open their envelope AFTER a long fast-path
        // block — deliberately, so the 304 branch creates nothing — so a short window reports them
        // as missing, which is exactly backwards.
        var next = source.IndexOf("\n    public ", start + 1, StringComparison.Ordinal);
        var window = next < 0 ? source[start..] : source[start..next];
        return window.Contains("InstanceReadActivityHelper.StartRead", StringComparison.Ordinal);
    }

    private static IEnumerable<string> PublicReadMethods() =>
        typeof(IInstanceQueryAppService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .Where(name => name.StartsWith("Get", StringComparison.Ordinal))
            .Distinct();

    private static string ReadQueryServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("could not locate the repository root");
        var path = Path.Combine(
            directory!.FullName,
            "src/BBT.Workflow.Application/Instances/InstanceQueryAppService.cs");

        File.Exists(path).ShouldBeTrue($"query service source not found at {path}");
        return File.ReadAllText(path);
    }
}
