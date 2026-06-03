using Microsoft.CodeAnalysis;

namespace CustomScriptHelpersDemo.Engine;

/// <summary>
/// Builds the curated <see cref="MetadataReference"/> list for sandboxed compilation
/// by filtering the runtime's Trusted Platform Assemblies down to the allow-list.
/// This is the opposite of the runtime's current behaviour, which references the
/// entire AppDomain — here a script can only see what we explicitly permit.
/// </summary>
public static class SandboxedReferenceSet
{
    public static IReadOnlyList<MetadataReference> Build(SandboxOptions options)
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var refs = new List<MetadataReference>();
        foreach (var path in tpa)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (options.AllowedAssemblies.Contains(name))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        return refs;
    }
}
