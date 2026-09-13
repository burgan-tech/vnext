using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Guards the registration rule: a new <c>ActivitySource</c> must reach every host's
/// <c>Telemetry:Tracing:AdditionalSources</c> in the same commit that introduces it.
/// <para>
/// This failure mode is silent and has already happened here. An unregistered source does not throw
/// and does not warn — <c>ActivitySource.StartActivity</c> simply returns <c>null</c>, the span is
/// never created, and everything that would have nested under it flattens onto the nearest ancestor.
/// It goes dark in ONE host only, so a test suite that runs against the orchestrator alone cannot
/// see it.
/// </para>
/// <para>
/// Deliberately catalogue-independent: it asserts the rule, not a list of span names, so it keeps
/// working as spans are added and is not maintenance attached to each one.
/// </para>
/// </summary>
public sealed class ActivitySourceRegistrationTests
{
    /// <summary>
    /// Registration is per-host by design, not global — the Execution host covers its own family
    /// with a wildcard, and the workers cover theirs. So the rule is "every declared source is
    /// matched by some entry", with wildcards honoured, rather than set equality.
    /// </summary>
    public static TheoryData<string, string> HostSettings() => new()
    {
        { "orchestration", "orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json" },
        { "execution", "execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json" },
        { "workers-inbox", "workers/BBT.Workflow.Workers.Inbox/appsettings.json" },
        { "workers-outbox", "workers/BBT.Workflow.Workers.Outbox/appsettings.json" }
    };

    private static bool IsExpectedIn(string source, string host) =>
        !TelemetryConstants.ActivitySources.DeliberatelyUnregistered.TryGetValue(source, out var absent)
        || !absent.Contains(host);

    [Theory]
    [MemberData(nameof(HostSettings))]
    public void EveryDeclaredSource_IsRegisteredInEveryHostThatCanEmitIt(string host, string relativePath)
    {
        var registered = ReadAdditionalSources(relativePath);

        var missing = TelemetryConstants.ActivitySources.All
            .Where(source => IsExpectedIn(source, host))
            .Where(source => !registered.Any(entry => Matches(entry, source)))
            .ToList();

        missing.ShouldBeEmpty(
            $"{relativePath} does not cover: {string.Join(", ", missing)}. " +
            "An unregistered source produces no span at all in that host — silently.");
    }

    /// <summary>
    /// The converse. A registered source with no emitter is not harmful at runtime, but it sends the
    /// next reader hunting for spans that cannot appear — which is why the removed EventHook source
    /// was deleted from all four files rather than left behind.
    /// </summary>
    [Theory]
    [MemberData(nameof(HostSettings))]
    public void NoRegisteredEntry_IsOrphaned(string host, string relativePath)
    {
        var orphaned = ReadAdditionalSources(relativePath)
            .Where(entry => !TelemetryConstants.ActivitySources.All.Any(source => Matches(entry, source)))
            .ToList();

        orphaned.ShouldBeEmpty(
            $"{relativePath} ({host}) registers sources nothing declares: {string.Join(", ", orphaned)}");
    }

    private static bool Matches(string entry, string source) =>
        entry.EndsWith('*')
            ? source.StartsWith(entry[..^1], StringComparison.Ordinal)
            : string.Equals(entry, source, StringComparison.Ordinal);

    private static List<string> ReadAdditionalSources(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        File.Exists(path).ShouldBeTrue($"host settings not found: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .GetProperty("Telemetry").GetProperty("Tracing").GetProperty("AdditionalSources")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToList();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("could not locate the repository root from the test output directory");
        return directory!.FullName;
    }
}
