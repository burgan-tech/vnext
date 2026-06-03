namespace CustomScriptHelpersDemo.Engine;

/// <summary>
/// Sandbox policy applied when compiling consumer-supplied source.
/// Two independent layers:
///   1. <see cref="AllowedAssemblies"/> — a reference allow-list. Anything whose
///      assembly is not referenced simply will not compile (e.g. HttpClient).
///   2. <see cref="BannedNamespaces"/> — a semantic ban for types that live inside
///      mandatory assemblies (System.Private.CoreLib) and therefore cannot be
///      blocked by reference omission (e.g. System.IO.File).
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>Simple assembly names (no extension) that scripts may reference.</summary>
    public HashSet<string> AllowedAssemblies { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Private.CoreLib", // mandatory — contains Object, String, Math, decimal...
        "System.Runtime",
        "System.Runtime.Extensions",
        "System.Collections",
        "System.Collections.Concurrent",
        "System.Linq",
        "System.Linq.Expressions",
        "System.ObjectModel",
        "System.Text.RegularExpressions",
        "System.Console",
        "System.Security.Cryptography", // allow RSA/AES etc. (crypto is permitted)
        "netstandard",
    };

    /// <summary>Namespace prefixes that scripts may not touch, even if reachable.</summary>
    public HashSet<string> BannedNamespaces { get; } = new(StringComparer.Ordinal)
    {
        "System.IO",
        "System.Net",
        "System.Diagnostics",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "Microsoft.Win32",
    };

    public bool AllowUnsafe => false;
}
