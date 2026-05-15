namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Resolves the appropriate <see cref="IDomainFunctionHandler"/> for a given function type.
/// Returns <c>null</c> when no handler matches — callers should fall back to default dispatch.
/// </summary>
public interface IDomainFunctionHandlerFactory
{
    /// <summary>
    /// Returns the handler registered for <paramref name="functionType"/>.
    /// Falls back to <see cref="DefaultDomainFunctionHandler"/> when no specialized handler matches.
    /// </summary>
    IDomainFunctionHandler Get(string functionType);
}
