namespace CustomScriptHelpersDemo.Engine;

/// <summary>
/// Sandbox policy, bound from <c>appsettings.json</c> (section <c>Scripting:Sandbox</c>).
/// Two independent layers:
///   1. <see cref="AllowedAssemblies"/> — a reference allow-list. Anything whose assembly is not
///      referenced will not compile (e.g. HttpClient).
///   2. <see cref="BannedNamespaces"/> — a semantic ban for types that live inside mandatory
///      assemblies (System.Private.CoreLib) and cannot be blocked by reference omission
///      (e.g. System.IO.File).
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>Whether <c>unsafe</c> code is permitted in scripts.</summary>
    public bool AllowUnsafe { get; set; }

    /// <summary>
    /// Directory of operator-approved third-party DLLs loaded DYNAMICALLY at runtime.
    /// In Docker this is a mounted volume (e.g. <c>./plugins:/app/assemblies:ro</c>); the host
    /// never references these assemblies. Relative paths resolve against the app base directory;
    /// overridable via the <c>SCRIPT_PLUGIN_DIR</c> environment variable.
    /// </summary>
    public string PluginDirectory { get; set; } = "plugins";

    /// <summary>Simple assembly names (no extension) that scripts may reference.</summary>
    public List<string> AllowedAssemblies { get; set; } = [];

    /// <summary>Namespace prefixes that scripts may not touch, even if reachable.</summary>
    public List<string> BannedNamespaces { get; set; } = [];
}
