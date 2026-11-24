using BBT.Aether.Results;
using DynamicExpresso;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>
/// Describes one family of Dynamic Expresso expression slots: the interpreter configuration (declared
/// parameters, return type, registered functions) every expression of that family is compiled with.
/// <see cref="Name"/> is part of the compile-cache key, so two signatures must never share a name.
/// </summary>
public sealed record ExpressionSignature(
    string Name,
    IReadOnlyList<Parameter> Parameters,
    Type ReturnType,
    IReadOnlyDictionary<string, Delegate> Functions);

/// <summary>A compiled expression: the shared delegate and the identifiers the text references.</summary>
/// <typeparam name="TDelegate">Delegate type the expression was compiled to.</typeparam>
/// <param name="Invoke">The compiled delegate; thread-safe and shared across callers.</param>
/// <param name="UsedIdentifiers">
/// Identifiers (declared parameters, functions and unknown names) the text references, from
/// <c>Interpreter.DetectIdentifiers</c> on the same interpreter configuration.
/// </param>
public sealed record CompiledExpression<TDelegate>(TDelegate Invoke, IReadOnlySet<string> UsedIdentifiers)
    where TDelegate : Delegate;

/// <summary>
/// Single, bounded compiler for every Dynamic Expresso expression slot. Compiled delegates are cached
/// by (signature name, text) with LRU eviction.
/// </summary>
public interface IExpressionCompiler
{
    /// <summary>
    /// Compiles <paramref name="text"/> against <paramref name="signature"/>, or returns the cached
    /// delegate. Fails (never throws) when the text does not parse or does not fit the delegate.
    /// </summary>
    Result<CompiledExpression<TDelegate>> Compile<TDelegate>(string text, ExpressionSignature signature)
        where TDelegate : Delegate;
}
