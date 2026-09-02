using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Functions;

/// <summary>
/// The scope gate every function surface must pass before it reveals or runs anything.
/// Extracted so the execute path and the info/contract discovery paths cannot drift: a caller denied
/// on one must be denied on the other.
/// <para>
/// This policy does <b>not</b> evaluate <c>function.roles</c>. Custom function invocation is not a
/// capability boundary the runtime enforces; <c>function.roles</c> is evaluated only by the
/// <c>authorize</c> function, which the middle tier consults.
/// </para>
/// </summary>
public interface IFunctionAccessPolicy
{
    /// <summary>
    /// Enforces the function's declared <c>scope</c> against the request shape.
    /// </summary>
    /// <param name="function">The resolved function definition.</param>
    /// <param name="instance">The instance the call is bound to; null for domain-scoped calls.</param>
    /// <param name="workflow">The instance's workflow; used to enforce Flow scope membership.</param>
    /// <param name="headers">Request headers. Unused by the default policy; kept for custom policies.</param>
    /// <param name="queryParameters">Request query parameters. Unused by the default policy; kept for custom policies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> AuthorizeAsync(
        Function function,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken = default);
}
