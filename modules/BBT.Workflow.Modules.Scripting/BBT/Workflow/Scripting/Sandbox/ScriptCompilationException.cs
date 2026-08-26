using System;

namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Thrown when a script fails to compile — Roslyn diagnostics with severity Error. The message
/// lists the offending diagnostics so a flow author can correct the script. Sandbox violations
/// throw the derived <see cref="ScriptSandboxViolationException"/>, so callers can distinguish
/// "the code is broken" from "the code is forbidden" while a single catch of this base type still
/// covers both.
/// </summary>
public class ScriptCompilationException : Exception
{
    public ScriptCompilationException(string message) : base(message)
    {
    }

    public ScriptCompilationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the <see cref="BannedApiAnalyzer"/> rejects a script before IL emission: banned
/// namespace usage, P/Invoke, or <c>unsafe</c> code. Derives from
/// <see cref="ScriptCompilationException"/> so existing handlers of the base type keep working.
/// </summary>
public sealed class ScriptSandboxViolationException : ScriptCompilationException
{
    public ScriptSandboxViolationException(string message) : base(message)
    {
    }
}
