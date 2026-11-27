using BBT.Aether.Results;
using DynamicExpresso;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>
/// Default <see cref="IExpressionCompiler"/>: one fresh <see cref="Interpreter"/> per compile configured
/// from the <see cref="ExpressionSignature"/>, results cached in a <see cref="BoundedLruCache{TKey,TValue}"/>.
/// Singleton; safe for concurrent use. A failed compile is never cached.
/// </summary>
public sealed class ExpressionCompiler : IExpressionCompiler
{
    /// <summary>Error code of a compile failure.</summary>
    public const string CompileFailedCode = "Expression.CompileFailed";

    /// <summary>
    /// Process-wide instance used when an evaluator is constructed without DI; shares one cache the way
    /// the former per-evaluator static dictionaries did.
    /// </summary>
    public static ExpressionCompiler Shared { get; } = new(new ExpressionCompilerOptions().CacheCapacity);

    // Keyed by delegate type too: the same (signature, text) compiled to two delegate types are two entries,
    // so alternating callers never recompile each other's entry.
    private readonly BoundedLruCache<(string Name, Type Delegate, string Text), object> _cache;

    /// <summary>Creates a compiler with the configured cache capacity.</summary>
    public ExpressionCompiler(IOptions<ExpressionCompilerOptions> options)
        : this(options.Value.CacheCapacity)
    {
    }

    /// <summary>Creates a compiler with an explicit cache capacity.</summary>
    public ExpressionCompiler(int cacheCapacity)
    {
        _cache = new BoundedLruCache<(string, Type, string), object>(cacheCapacity);
    }

    /// <summary>Number of compiled expressions currently cached.</summary>
    public int CacheCount => _cache.Count;

    /// <inheritdoc />
    public Result<CompiledExpression<TDelegate>> Compile<TDelegate>(string text, ExpressionSignature signature)
        where TDelegate : Delegate
    {
        var key = (signature.Name, typeof(TDelegate), text);
        if (_cache.TryGet(key, out var cached) && cached is CompiledExpression<TDelegate> hit)
            return Result<CompiledExpression<TDelegate>>.Ok(hit);

        try
        {
            var interpreter = new Interpreter(InterpreterOptions.Default).EnableAssignment(signature.Assignment);
            foreach (var (name, function) in signature.Functions)
                interpreter.SetFunction(name, function);

            var parameters = signature.Parameters.ToArray();
            var lambda = interpreter.Parse(text, signature.ReturnType, parameters);
            var invoke = lambda.Compile<TDelegate>();

            var identifiers = interpreter.DetectIdentifiers(text);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identifier in identifiers.Identifiers)
                used.Add(identifier.Name);
            foreach (var identifier in identifiers.UnknownIdentifiers)
                used.Add(identifier);

            var compiled = new CompiledExpression<TDelegate>(invoke, used);
            return Result<CompiledExpression<TDelegate>>.Ok((CompiledExpression<TDelegate>)_cache.GetOrAdd(key, compiled));
        }
        catch (Exception ex)
        {
            return Result<CompiledExpression<TDelegate>>.Fail(Error.Validation(CompileFailedCode, ex.Message));
        }
    }
}
