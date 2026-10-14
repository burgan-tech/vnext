using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Simple-name → path listing of the operator-mounted plugin directory, shared by the compile side
/// (<see cref="SandboxedReferenceSet"/>) and the load side (the plugin load context), so a name both
/// compiles and loads from the same file.
/// <para>
/// One listing per directory, taken on first use and kept for the process lifetime. A lookup that
/// misses re-reads the directory, at most once per <see cref="ScriptSandboxOptions.PluginRescanIntervalSeconds"/>:
/// a pod that started before the plugin volume was populated (the Helm populator Job runs as a
/// post-install hook) then finds the DLLs without a restart, and a name that never resolves costs one
/// listing per interval. A hit never re-reads, so the steady state does no filesystem I/O.
/// </para>
/// </summary>
internal static class ScriptPluginDirectory
{
    private static readonly ConcurrentDictionary<string, Listing> ListingsByDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path of the plugin DLL named <paramref name="simpleName"/>, or false when there is none.</summary>
    public static bool TryGetPath(ScriptSandboxOptions options, string simpleName, out string path)
    {
        path = string.Empty;
        var directory = options.ResolvePluginDirectory();
        if (string.IsNullOrEmpty(directory) || string.IsNullOrWhiteSpace(simpleName))
            return false;

        var listing = ListingsByDirectory.GetOrAdd(directory, static dir => new Listing(dir));
        return listing.TryGetPath(simpleName, TimeSpan.FromSeconds(Math.Max(0, options.PluginRescanIntervalSeconds)), out path);
    }

    private sealed class Listing(string directory)
    {
        private readonly object _scanLock = new();
        private volatile Dictionary<string, string> _map = Scan(directory);
        // Read and written only under _scanLock.
        private long _scannedAt = Environment.TickCount64;

        public bool TryGetPath(string simpleName, TimeSpan rescanInterval, out string path)
        {
            if (_map.TryGetValue(simpleName, out path!))
                return true;

            lock (_scanLock)
            {
                // Another miss may have rescanned while this one waited for the lock.
                if (_map.TryGetValue(simpleName, out path!))
                    return true;

                if (Environment.TickCount64 - _scannedAt < (long)rescanInterval.TotalMilliseconds)
                    return false;

                _map = Scan(directory);
                _scannedAt = Environment.TickCount64;
                return _map.TryGetValue(simpleName, out path!);
            }
        }

        private static Dictionary<string, string> Scan(string dir)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(dir))
                foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                    map.TryAdd(Path.GetFileNameWithoutExtension(dll), dll);

            return map;
        }
    }
}
