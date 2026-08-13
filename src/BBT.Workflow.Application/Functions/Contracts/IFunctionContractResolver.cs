using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Functions.Contracts;

/// <summary>
/// Resolves which component reference a function contract slot points at for the current request,
/// by evaluating the slot's rule-based entries in declaration order.
/// </summary>
public interface IFunctionContractResolver
{
    /// <summary>
    /// Evaluates the entries of <paramref name="slot"/> in declaration order and returns the first
    /// match. A rule-less entry always matches, so a trailing rule-less entry acts as the fallback.
    /// </summary>
    /// <returns>
    /// <c>Ok(null)</c> when the slot declares nothing or no entry matched - a function may legitimately
    /// have no applicable view or schema for a given request, which is not an error. Fails only when
    /// the script context could not be built.
    /// </returns>
    Task<Result<FunctionContractResolution?>> ResolveAsync(
        Function function,
        FunctionContractSlot slot,
        LazyScriptContext scriptContext,
        CancellationToken cancellationToken = default);
}
