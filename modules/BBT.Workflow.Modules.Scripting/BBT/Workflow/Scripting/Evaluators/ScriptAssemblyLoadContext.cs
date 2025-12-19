using System;
using System.Reflection;
using System.Runtime.Loader;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// A collectible AssemblyLoadContext that allows dynamic script assemblies to be unloaded
/// when they are no longer referenced. This prevents memory leaks from accumulated script compilations.
/// </summary>
internal sealed class ScriptAssemblyLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Creates a new collectible script assembly load context.
    /// </summary>
    /// <param name="name">Optional name for debugging purposes</param>
    public ScriptAssemblyLoadContext(string? name = null) 
        : base(name ?? $"ScriptContext-{Guid.NewGuid():N}", isCollectible: true)
    {
    }

    /// <summary>
    /// Resolves assembly references. Returns null to use default resolution.
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Return null to fall back to default resolution
        // This allows the script to use assemblies from the main application
        return null;
    }
}

