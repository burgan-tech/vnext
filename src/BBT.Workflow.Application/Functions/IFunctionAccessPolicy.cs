using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Functions;

/// <summary>
/// The scope and role gates every function surface must pass before it reveals or runs anything.
/// Extracted so the execute path and the info/contract discovery paths cannot drift: a caller denied
/// on one must be denied on the other, and a function's shape must never leak to a caller who could
/// not invoke it.
/// </summary>
public interface IFunctionAccessPolicy
{
    /// <summary>
    /// Enforces the function's declared <c>scope</c> against the request shape and, when the function
    /// declares <c>roles</c>, evaluates the caller's roles against the grant set.
    /// </summary>
    /// <param name="function">The resolved function definition.</param>
    /// <param name="instance">The instance the call is bound to; null for domain-scoped calls.</param>
    /// <param name="workflow">The instance's workflow; used to enforce Flow scope membership.</param>
    /// <param name="headers">Request headers - carries the legacy <c>role</c> header fallback.</param>
    /// <param name="queryParameters">Request query parameters, for dynamic role grant navigation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> AuthorizeAsync(
        Function function,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken = default);
}
