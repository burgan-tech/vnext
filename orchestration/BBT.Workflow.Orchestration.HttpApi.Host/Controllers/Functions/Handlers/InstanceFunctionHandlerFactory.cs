namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Default implementation of <see cref="IInstanceFunctionHandlerFactory"/>.
/// Resolves handlers from the DI-registered <see cref="IEnumerable{IInstanceFunctionHandler}"/> collection.
/// </summary>
public sealed class InstanceFunctionHandlerFactory(
    IEnumerable<IInstanceFunctionHandler> handlers) : IInstanceFunctionHandlerFactory
{
    public IInstanceFunctionHandler? Get(string functionType)
        => handlers.FirstOrDefault(h => h.FunctionType == functionType);
}
