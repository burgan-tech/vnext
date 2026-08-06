using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Definitions;

/// <summary>
/// Pins that every client-facing URL template has a key in every host's <c>UrlTemplates</c>
/// configuration.
/// <para>
/// <see cref="UrlTemplateOptions"/> holds base paths with no gateway prefix, because the prefix is a
/// deployment concern and differs per host (<c>/api/…</c> for orchestration, <c>/api/v1/monitor/…</c>
/// for the monitor). That design is correct, but it makes the config the only place the prefix exists —
/// so a template added to the options class without a matching config key silently falls back to the
/// prefix-less base path and emits <c>/{domain}/…</c> while every sibling href carries the prefix.
/// </para>
/// <para>
/// That is precisely how the function catalog, info, view and schema hrefs regressed. This test makes
/// the omission a build failure. Reflection is used deliberately so a template added later is covered
/// without anyone remembering to extend this test.
/// </para>
/// </summary>
public class UrlTemplateConfigCompletenessTests
{
    private static readonly string[] HostAppSettings =
    [
        "orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json",
        "monitoring/BBT.Workflow.Monitor.HttpApi.Host/appsettings.json",
    ];

    private static IEnumerable<string> TemplateNames => typeof(UrlTemplateOptions)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string))
        .Select(p => p.Name);

    public static TheoryData<string> Hosts() => [.. HostAppSettings];

    [Theory]
    [MemberData(nameof(Hosts))]
    public void EveryTemplate_HasAConfigKeyInEveryHost(string relativePath)
    {
        var configured = ReadUrlTemplates(relativePath);

        var missing = TemplateNames.Where(n => !configured.ContainsKey(n)).ToList();

        missing.ShouldBeEmpty(
            $"'{relativePath}' is missing UrlTemplates keys, so these templates would fall back to the " +
            $"prefix-less base path and emit hrefs without the host's gateway prefix: " +
            string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void NoConfigKey_IsOrphaned(string relativePath)
    {
        var configured = ReadUrlTemplates(relativePath);
        var known = TemplateNames.ToHashSet(StringComparer.Ordinal);

        var orphaned = configured.Keys.Where(k => !known.Contains(k)).ToList();

        orphaned.ShouldBeEmpty(
            $"'{relativePath}' configures UrlTemplates keys that no longer exist on UrlTemplateOptions " +
            $"(renamed or removed?), so they are silently ignored: " + string.Join(", ", orphaned));
    }

    /// <summary>
    /// Every configured template must share one prefix within a host, so sibling hrefs in the same
    /// response cannot disagree. The prefix itself is the host's business — this only checks agreement.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void AllTemplatesInAHost_ShareTheSamePrefix(string relativePath)
    {
        var configured = ReadUrlTemplates(relativePath);

        // Everything before the "{0}" domain placeholder is the host's prefix.
        var prefixes = configured
            .Select(kv => (kv.Key, Prefix: kv.Value[..kv.Value.IndexOf("{0}", StringComparison.Ordinal)]))
            .ToList();

        var distinct = prefixes.Select(p => p.Prefix).Distinct(StringComparer.Ordinal).ToList();

        distinct.Count.ShouldBe(1,
            $"'{relativePath}' mixes prefixes across templates: " +
            string.Join(", ", prefixes.Select(p => $"{p.Key}='{p.Prefix}'")));
    }

    private static Dictionary<string, string> ReadUrlTemplates(string relativePath)
    {
        var full = Path.Combine(RepoRoot(), relativePath);
        File.Exists(full).ShouldBeTrue($"Expected host configuration at '{full}'.");

        using var doc = JsonDocument.Parse(File.ReadAllText(full));

        doc.RootElement.TryGetProperty("UrlTemplates", out var section)
            .ShouldBeTrue($"'{relativePath}' has no UrlTemplates section.");

        return section.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    /// <summary>
    /// Walks up from the test assembly to the directory holding the solution file. The host
    /// appsettings are not copied into the test output, so they are read from the working tree.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BBT.Workflow.slnx")))
            dir = dir.Parent;

        dir.ShouldNotBeNull("Could not locate the repository root (BBT.Workflow.slnx) above the test assembly.");
        return dir!.FullName;
    }
}
