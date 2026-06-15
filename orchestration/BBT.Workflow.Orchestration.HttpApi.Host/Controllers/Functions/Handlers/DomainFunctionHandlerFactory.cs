namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Default implementation of <see cref="IDomainFunctionHandlerFactory"/>.
/// Resolves handlers from the DI-registered <see cref="IEnumerable{IDomainFunctionHandler}"/> collection.
/// Falls back to <see cref="DefaultDomainFunctionHandler"/> when no specialized handler matches.
/// </summary>
public sealed class DomainFunctionHandlerFactory(
    IEnumerable<IDomainFunctionHandler> handlers) : IDomainFunctionHandlerFactory
{
    public IDomainFunctionHandler Get(string functionType)
        => handlers.FirstOrDefault(h => h.FunctionType == functionType)
           ?? handlers.First(h => h is DefaultDomainFunctionHandler);
}
