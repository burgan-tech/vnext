namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Answers whether an assembly simple name can be referenced by a script compile in this runtime.
/// Used at publish time to reject a <c>scripts.allowedAssemblies</c> entry that the compiler would
/// otherwise drop silently (see <see cref="SandboxedReferenceSet.Build"/>).
/// </summary>
public interface IScriptAssemblyCatalog
{
    /// <summary>True when <paramref name="simpleName"/> (no extension, case-insensitive) resolves.</summary>
    bool IsAvailable(string simpleName);
}
