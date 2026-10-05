namespace BBT.Workflow.Scripting;

/// <summary>
/// Roslyn contract for a CacheAside key script (object form of <c>key</c> with a non-<c>dynamicExpresso</c>
/// location). Returns the cache key; null or whitespace keeps the previously resolved key.
/// </summary>
public interface ICacheKeyMapping
{
    /// <summary>Computes the cache key from the script context.</summary>
    Task<string?> Handler(ScriptContext context);
}
