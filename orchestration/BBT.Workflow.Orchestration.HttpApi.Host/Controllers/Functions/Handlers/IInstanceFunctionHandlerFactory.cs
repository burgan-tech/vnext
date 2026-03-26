namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Resolves the appropriate <see cref="IInstanceFunctionHandler"/> for a given system function type.
/// Returns <c>null</c> when no handler matches — callers should fall back to custom function dispatch.
/// </summary>
public interface IInstanceFunctionHandlerFactory
{
    /// <summary>
    /// Returns the handler registered for <paramref name="functionType"/>, or <c>null</c> if none is registered.
    /// </summary>
    IInstanceFunctionHandler? Get(string functionType);
}
