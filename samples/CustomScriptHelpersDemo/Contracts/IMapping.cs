namespace CustomScriptHelpersDemo.Contracts;

/// <summary>
/// Minimal stand-in for the runtime's IMapping contract. A consumer-authored
/// mapping script implements this and is compiled at runtime.
/// </summary>
public interface IMapping
{
    Task<ScriptResponse> InputHandler(ScriptContext context);
}

/// <summary>Simplified script execution context passed to the mapping.</summary>
public sealed class ScriptContext
{
    public required string TransitionKey { get; init; }
    public Dictionary<string, object?> Data { get; init; } = new();
}

/// <summary>Simplified mapping result.</summary>
public sealed class ScriptResponse
{
    public object? Data { get; init; }
}
