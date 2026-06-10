using System;

namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Thrown when sandboxed compilation fails — either Roslyn compile errors or one or more
/// sandbox violations detected by <see cref="BannedApiAnalyzer"/>. The message lists the offending
/// items so a flow author can correct the script.
/// </summary>
public sealed class ScriptCompilationException : Exception
{
    public ScriptCompilationException(string message) : base(message)
    {
    }

    public ScriptCompilationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
