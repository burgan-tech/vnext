using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// The one load context per plugin directory that holds every plugin DLL scripts use (a database
/// driver, for instance). Script and helper contexts delegate plugin names here, so a plugin is loaded
/// once per process: its static state — a driver's connection pool above all — is shared by every
/// script and helper set, and a helper republish does not load a fresh copy beside the old one.
/// <para>
/// Non-collectible on purpose: drivers keep timers, pools and event sources alive, and nothing could
/// unload them safely while scripts may still hold their types.
/// </para>
/// <para>
/// Inside this context a DLL in the plugin directory always wins, framework-named or not — the rule
/// helper sets had when they preloaded every plugin DLL into their own context, so a driver resolves
/// its dependencies exactly as it did there. Whether a framework-named plugin is served to a caller is
/// the caller's choice (<see cref="Resolve"/>): helper sets keep that old behaviour, helper-less
/// scripts — which never saw plugins — keep resolving host names to the host.
/// </para>
/// </summary>
internal sealed class ScriptPluginLoadContext : AssemblyLoadContext
{
    private static readonly ConcurrentDictionary<string, ScriptPluginLoadContext> ByDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ScriptSandboxOptions _options;

    /// <summary>Names already reported, so a failing plugin logs once rather than on every lookup.</summary>
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.OrdinalIgnoreCase);

    private volatile ILogger _logger = NullLogger.Instance;

    private ScriptPluginLoadContext(ScriptSandboxOptions options, string directory)
        : base($"ScriptPlugins:{directory}", isCollectible: false)
    {
        _options = options;
    }

    /// <summary>
    /// The plugin context for <paramref name="options"/>' directory; null when none is configured.
    /// The context is process-wide, so the first real <paramref name="logger"/> supplied is kept.
    /// </summary>
    public static ScriptPluginLoadContext? For(ScriptSandboxOptions options, ILogger? logger = null)
    {
        var directory = options.ResolvePluginDirectory();
        if (string.IsNullOrEmpty(directory))
            return null;

        var context = ByDirectory.GetOrAdd(directory, dir => new ScriptPluginLoadContext(options, dir));
        if (logger is not null && context._logger is NullLogger)
            context._logger = logger;

        return context;
    }

    /// <summary>
    /// The plugin assembly for <paramref name="assemblyName"/>, or null when the name is not a plugin
    /// (the caller then falls back to default resolution). A plugin DLL named like a framework/host
    /// assembly is served only when <paramref name="includeFrameworkNames"/> is true.
    /// </summary>
    public Assembly? Resolve(AssemblyName assemblyName, bool includeFrameworkNames)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name)
            || (!includeFrameworkNames && SandboxedReferenceSet.IsFrameworkAssembly(name))
            || !ScriptPluginDirectory.TryGetPath(_options, name, out _))
        {
            return null;
        }

        try
        {
            // Goes through this context's cache, so concurrent callers get the one loaded instance;
            // only a first load reaches Load below.
            return LoadFromAssemblyName(assemblyName);
        }
        catch (Exception ex) when (ex is BadImageFormatException or System.IO.FileLoadException or System.IO.FileNotFoundException)
        {
            // An unloadable plugin DLL falls back to default resolution, as the old preload did per
            // file — but logged, so the real cause (a stub or wrong-version DLL) is not lost behind
            // the FileNotFoundException the script sees later. Load below usually reported it first.
            if (_reported.TryAdd(name, 0))
                ScriptPluginLogs.ScriptPluginResolveFailed(_logger, ex, name);
            return null;
        }
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name) || !ScriptPluginDirectory.TryGetPath(_options, name, out var path))
            return null;

        try
        {
            return LoadFromAssemblyPath(path);
        }
        catch (Exception ex) when (ex is BadImageFormatException or System.IO.FileLoadException or System.IO.FileNotFoundException)
        {
            // Same per-file tolerance as Resolve: an unloadable copy (a framework DLL dumped into the
            // plugin directory, say) falls back to the host's own assembly. Logged once per name.
            if (_reported.TryAdd(name, 0))
                ScriptPluginLogs.ScriptPluginLoadFailed(_logger, ex, name, path);
            return null;
        }
    }
}
