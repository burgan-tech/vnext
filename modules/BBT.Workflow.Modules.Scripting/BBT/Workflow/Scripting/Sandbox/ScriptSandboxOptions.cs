using System;
using System.Collections.Generic;

namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Sandbox policy for script compilation, bound from configuration (section <c>Scripting:Sandbox</c>).
///
/// Two independent compile-time layers gate a script:
/// <list type="number">
/// <item><see cref="AllowedAssemblies"/> — a reference allow-list. Anything whose assembly is not
/// referenced will not compile (e.g. <c>System.Net.Http.HttpClient</c>).</item>
/// <item>A banned-namespace analyzer (<see cref="Sandbox.BannedApiAnalyzer"/>) — a semantic ban for
/// dangerous types that live inside mandatory assemblies (e.g. <c>System.IO.File</c> in
/// <c>System.Private.CoreLib</c>) which reference omission alone cannot block.</item>
/// </list>
///
/// The platform owns a mandatory, non-overridable banned-namespace baseline
/// (<see cref="Sandbox.BannedApiAnalyzer.MandatoryBannedNamespaces"/>). <see cref="BannedNamespaces"/>
/// only <b>adds</b> to that baseline; it can never remove an entry from it.
/// </summary>
public sealed class ScriptSandboxOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Scripting:Sandbox";

    /// <summary>
    /// Master switch. When <c>false</c> (default) the legacy compile path is used unchanged
    /// (full AppDomain references, no analyzer) so existing deployments are unaffected.
    /// When <c>true</c>, <b>all</b> script compilations use the restricted reference set and the
    /// banned-API analyzer.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Whether <c>unsafe</c> code is permitted in scripts. Defaults to <c>false</c>.</summary>
    public bool AllowUnsafe { get; set; }

    /// <summary>
    /// Directory of operator-approved third-party DLLs loaded dynamically at runtime (a mounted
    /// volume, not a host dependency). Relative paths resolve against the app base directory.
    /// </summary>
    public string PluginDirectory { get; set; } = "plugins";

    /// <summary>
    /// Global baseline of simple assembly names (no extension) that scripts may reference.
    /// A per-mapping grant is merged on top of this for an individual compile.
    /// </summary>
    public List<string> AllowedAssemblies { get; set; } = [];

    /// <summary>
    /// Additional banned namespace prefixes, merged on top of the mandatory platform baseline.
    /// Cannot remove entries from the mandatory baseline.
    /// </summary>
    public List<string> BannedNamespaces { get; set; } = [];

    /// <summary>
    /// Resolves <see cref="PluginDirectory"/> to an absolute path against the app base directory
    /// when it is relative.
    /// </summary>
    public string ResolvePluginDirectory()
    {
        if (string.IsNullOrWhiteSpace(PluginDirectory))
            return string.Empty;

        return System.IO.Path.IsPathRooted(PluginDirectory)
            ? PluginDirectory
            : System.IO.Path.Combine(AppContext.BaseDirectory, PluginDirectory);
    }
}
