namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Default <see cref="IScriptAssemblyCatalog"/>: the exact universe the sandboxed compile resolves
/// against — framework TPA plus the operator-mounted plugin directory — through
/// <see cref="SandboxedReferenceSet.IsResolvable"/>, so publish and compile can never disagree.
/// </summary>
public sealed class SandboxScriptAssemblyCatalog(ScriptSandboxOptions options) : IScriptAssemblyCatalog
{
    /// <inheritdoc />
    public bool IsAvailable(string simpleName) => SandboxedReferenceSet.IsResolvable(options, simpleName);
}
