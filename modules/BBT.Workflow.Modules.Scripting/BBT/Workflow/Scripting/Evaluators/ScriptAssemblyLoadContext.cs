using System;
using System.Reflection;
using System.Runtime.Loader;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// The load context compiled scripts and helper sets live in. Plugin assemblies are delegated to the
/// process-wide <see cref="ScriptPluginLoadContext"/> so every script shares one copy of each plugin;
/// every other name falls back to default resolution (the host's own assemblies).
/// </summary>
internal sealed class ScriptAssemblyLoadContext : AssemblyLoadContext
{
    private readonly ScriptPluginLoadContext? _plugins;
    private readonly bool _pluginsOverrideFramework;

    /// <summary>
    /// Creates a script load context.
    /// </summary>
    /// <param name="name">Optional name for debugging purposes</param>
    /// <param name="plugins">Plugin context to delegate plugin names to; null when no plugin directory is configured</param>
    /// <param name="pluginsOverrideFramework">
    /// Whether a plugin DLL named like a framework/host assembly is served instead of the host's copy.
    /// True for helper sets (their behaviour since plugins were preloaded into them), false for
    /// helper-less scripts (which never resolved plugins before)
    /// </param>
    /// <param name="isCollectible">Whether the context can be unloaded</param>
    public ScriptAssemblyLoadContext(
        string? name = null,
        ScriptPluginLoadContext? plugins = null,
        bool pluginsOverrideFramework = false,
        bool isCollectible = true)
        : base(name ?? $"ScriptContext-{Guid.NewGuid():N}", isCollectible)
    {
        _plugins = plugins;
        _pluginsOverrideFramework = pluginsOverrideFramework;
    }

    /// <summary>
    /// Resolves a plugin name from the plugin context; returns null for anything else so the default
    /// context resolves it (the script uses the main application's assemblies).
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName) =>
        _plugins?.Resolve(assemblyName, _pluginsOverrideFramework);
}
