using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Infrastructure.Definitions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Definitions;

/// <summary>
/// Pins that every client-facing URL template carries the host's base prefix, and that a template added
/// to <see cref="UrlTemplateOptions"/> is actually wired into <see cref="UrlTemplateBuilder"/>.
/// <para>
/// A host declares one <see cref="UrlTemplateOptions.BasePath"/>; every template inherits it from the
/// built-in route shape in <see cref="UrlTemplateDefaults"/>. That inheritance is what closed the old
/// failure mode — a template present on the options class but missing from a host's config used to fall
/// back to a prefix-less base path and emit <c>/{domain}/…</c> while every sibling href carried the
/// prefix, which is how the function catalog / info / view / schema hrefs regressed.
/// </para>
/// <para>
/// What can still go wrong is a new property that nobody reads in the builder's constructor, so the
/// operator's override is silently ignored. That is what <see cref="EveryOverrideProperty_IsWiredIntoTheBuilder"/>
/// catches. Reflection is used deliberately throughout so a template added later is covered without
/// anyone remembering to extend this test.
/// </para>
/// </summary>
public class UrlTemplateConfigCompletenessTests
{
    private static readonly string[] HostAppSettings =
    [
        "orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json",
        "monitoring/BBT.Workflow.Monitor.HttpApi.Host/appsettings.json",
    ];

    /// <summary>
    /// The per-endpoint override properties — every settable string property except the base path itself.
    /// </summary>
    private static IEnumerable<PropertyInfo> OverrideProperties => typeof(UrlTemplateOptions)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.CanWrite)
        .Where(p => p.Name != nameof(UrlTemplateOptions.BasePath));

    public static TheoryData<string> Hosts() => [.. HostAppSettings];

    public static TheoryData<string> OverrideNames() => [.. OverrideProperties.Select(p => p.Name)];

    /// <summary>
    /// With nothing configured at all, the application serves hrefs under its own prefix. This is the
    /// case for the orchestration host, which no longer declares a <c>UrlTemplates</c> section.
    /// </summary>
    [Fact]
    public void DefaultOptions_PrefixEveryHrefWithApiV1()
    {
        var hrefs = BuildEveryHref(new UrlTemplateOptions());

        foreach (var (method, href) in hrefs)
            href.ShouldStartWith(UrlTemplateDefaults.BasePath + "/", customMessage: $"{method} dropped the default base path.");
    }

    /// <summary>
    /// A single <c>BasePath</c> entry is all a host needs — every href, including the ones with no
    /// dedicated template such as the long-poll acknowledge URL, must pick it up.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/monitor")]
    [InlineData("api/v1/monitor/")]
    [InlineData("/gateway")]
    public void ConfiguredBasePath_PrefixesEveryHref(string basePath)
    {
        var hrefs = BuildEveryHref(new UrlTemplateOptions { BasePath = basePath });
        var expected = "/" + basePath.Trim('/');

        foreach (var (method, href) in hrefs)
            href.ShouldStartWith(expected + "/", customMessage: $"{method} did not carry the configured base path.");
    }

    /// <summary>
    /// An empty base path is legitimate for a host mounted at the root, and must not leave a stray slash
    /// or fall back to the default prefix.
    /// </summary>
    [Fact]
    public void EmptyBasePath_EmitsPrefixLessHrefs()
    {
        var hrefs = BuildEveryHref(new UrlTemplateOptions { BasePath = "" });

        foreach (var (method, href) in hrefs)
            href.ShouldStartWith("/domain", customMessage: $"{method} did not emit a prefix-less path.");
    }

    /// <summary>
    /// Every override property must be read somewhere in the builder. A property nobody reads looks
    /// configurable to an operator and does nothing — the quietest kind of failure, since the href stays
    /// plausible. Setting one property at a time and demanding the sentinel surface in some href proves
    /// the wiring without hard-coding a property-to-method map that would itself go stale.
    /// </summary>
    [Theory]
    [MemberData(nameof(OverrideNames))]
    public void EveryOverrideProperty_IsWiredIntoTheBuilder(string propertyName)
    {
        const string sentinel = "/sentinel-override";

        var options = new UrlTemplateOptions();
        typeof(UrlTemplateOptions).GetProperty(propertyName)!.SetValue(options, sentinel);

        var hrefs = BuildEveryHref(options);

        hrefs.Any(h => h.Href.StartsWith(sentinel, StringComparison.Ordinal)).ShouldBeTrue(
            $"'{propertyName}' is never read by UrlTemplateBuilder, so configuring it has no effect.");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void NoConfigKey_IsOrphaned(string relativePath)
    {
        var configured = ReadUrlTemplates(relativePath);
        var known = OverrideProperties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        known.Add(nameof(UrlTemplateOptions.BasePath));

        var orphaned = configured.Keys.Where(k => !known.Contains(k)).ToList();

        orphaned.ShouldBeEmpty(
            $"'{relativePath}' configures UrlTemplates keys that no longer exist on UrlTemplateOptions " +
            $"(renamed or removed?), so they are silently ignored: " + string.Join(", ", orphaned));
    }

    /// <summary>
    /// A per-endpoint override earns its place only by differing from what the base path already
    /// produces. Restating the default is how the section grew to nineteen near-identical lines per host,
    /// and each restated line is another place to forget when a route changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void HostConfig_DoesNotRestateADefault(string relativePath)
    {
        var configured = ReadUrlTemplates(relativePath);

        if (!configured.TryGetValue(nameof(UrlTemplateOptions.BasePath), out var basePath))
            basePath = UrlTemplateDefaults.BasePath;

        var normalizedBase = basePath.Length == 0 ? "" : "/" + basePath.Trim('/');

        var redundant = configured
            .Where(kv => kv.Key != nameof(UrlTemplateOptions.BasePath))
            .Where(kv => kv.Value == normalizedBase + BuiltInDefaultFor(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        redundant.ShouldBeEmpty(
            $"'{relativePath}' overrides templates with exactly what BasePath already yields; drop these " +
            $"keys and let them inherit: " + string.Join(", ", redundant));
    }

    private static string BuiltInDefaultFor(string templateName)
        => (string)typeof(UrlTemplateDefaults)
            .GetField(templateName, BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

    /// <summary>
    /// Invokes every <c>Build*Url</c> member of <see cref="IUrlTemplateBuilder"/> with placeholder
    /// arguments. Going through the interface rather than the class covers the default interface methods
    /// too — <c>BuildLongPollAckUrl</c> has no backing template and would otherwise escape every
    /// assertion here.
    /// </summary>
    private static List<(string Method, string Href)> BuildEveryHref(UrlTemplateOptions options)
    {
        IUrlTemplateBuilder builder = new UrlTemplateBuilder(Options.Create(options));

        var results = typeof(IUrlTemplateBuilder)
            .GetMethods()
            .Where(m => m.Name.StartsWith("Build", StringComparison.Ordinal))
            .Select(m => (m.Name, Href: (string)m.Invoke(builder, ArgumentsFor(m))!))
            .ToList();

        results.ShouldNotBeEmpty("Reflection found no Build*Url methods — the discovery above is broken.");
        return results;
    }

    private static object?[] ArgumentsFor(MethodInfo method) => method
        .GetParameters()
        .Select(p => p.ParameterType == typeof(IEnumerable<string>)
            ? (object?)new[] { "ext" }
            : p.Name switch
            {
                "domain" => "domain",
                "workflow" => "workflow",
                "instance" or "instanceId" => "instance",
                "transitionKey" => "transition",
                "function" => "function",
                "target" => "input",
                _ => null, // apiVersionPrefix — the templates already carry the prefix.
            })
        .ToArray();

    private static Dictionary<string, string> ReadUrlTemplates(string relativePath)
    {
        var full = Path.Combine(RepoRoot(), relativePath);
        File.Exists(full).ShouldBeTrue($"Expected host configuration at '{full}'.");

        using var doc = JsonDocument.Parse(File.ReadAllText(full));

        // A host with no section at all is valid — it inherits the application's own prefix.
        if (!doc.RootElement.TryGetProperty("UrlTemplates", out var section))
            return [];

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
