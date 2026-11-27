using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>Settings of <see cref="IExpressionCompiler"/>. Bound from "Workflow:Expressions".</summary>
public sealed class ExpressionCompilerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Workflow:Expressions";

    /// <summary>Maximum number of compiled expressions kept (LRU eviction beyond it).</summary>
    [Range(1, int.MaxValue, ErrorMessage = "CacheCapacity must be at least 1")]
    public int CacheCapacity { get; set; } = 4096;
}
