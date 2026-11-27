using System.Runtime.CompilerServices;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Expressions;

namespace BBT.Workflow.Scripting.Declarative;

/// <summary>
/// Process-wide memo of compiled declarative plans, keyed by the <see cref="MappingBlock"/> INSTANCE (reference
/// identity, never record equality) and the <see cref="DeclarativeSiteKind"/>. Definitions are the component cache's
/// objects, so a block lives as long as its definition is cached and a republished definition brings new block
/// instances (and new plans); the <see cref="ConditionalWeakTable{TKey,TValue}"/> lets a dropped definition's plans go
/// with it. Only successful builds are kept — a failing block is rebuilt (and fails the same way) on each call.
/// </summary>
/// <remarks>
/// Thread-safe. Two callers racing on a new block may both build; one plan wins and the other is discarded — plans
/// are immutable and their delegates come from the shared compiler, so either is correct.
/// </remarks>
internal static class DeclarativePlanCache
{
    private static readonly int KindCount = Enum.GetValues<DeclarativeSiteKind>().Length;

    private static readonly ConditionalWeakTable<MappingBlock, DeclarativeMappingPlan?[]> Plans = new();

    /// <summary>The cached plan of <paramref name="block"/> for <paramref name="kind"/>, built on first use.</summary>
    public static Result<DeclarativeMappingPlan> GetOrBuild(
        IExpressionCompiler compiler, MappingBlock block, DeclarativeSiteKind kind)
    {
        var slots = Plans.GetValue(block, static _ => new DeclarativeMappingPlan?[KindCount]);
        var index = (int)kind;
        if (Volatile.Read(ref slots[index]) is { } cached)
            return Result<DeclarativeMappingPlan>.Ok(cached);

        var built = DeclarativeMappingPlan.Build(compiler, block, kind);
        if (!built.IsSuccess)
            return built;

        var winner = Interlocked.CompareExchange(ref slots[index], built.Value, null) ?? built.Value!;
        return Result<DeclarativeMappingPlan>.Ok(winner);
    }
}
