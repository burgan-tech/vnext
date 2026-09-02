using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Functions;

/// <summary>
/// Default <see cref="IFunctionAccessPolicy"/>: scope enforcement only.
/// <para>
/// Role evaluation deliberately does not happen here. Custom function invocation is not a capability
/// boundary the runtime owns — that belongs to the middle tier. vNext's contribution is visibility
/// (the function catalog) and the <c>authorize</c> function, which is where <c>function.roles</c> is
/// still evaluated. Removing the gate here means a caller may execute a function whose <c>roles</c>
/// would answer "denied" from <c>authorize</c>; that split is intentional.
/// </para>
/// </summary>
public sealed class FunctionAccessPolicy : IFunctionAccessPolicy
{
    /// <inheritdoc />
    public Task<Result> AuthorizeAsync(
        Function function,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken = default)
    {
        // Scope enforcement. Domain is exempt; Instance/Flow require an instance;
        // Flow additionally requires the function to be declared in the instance's flow.
        // This validates the shape of the call, not the caller's authority.
        if (!function.Scope.Equals(TaskScope.Domain))
        {
            if (instance == null)
                return Task.FromResult(Result.Fail(
                    WorkflowErrors.FunctionScopeNotSatisfied(function.Key, function.Scope.Description)));

            if (function.Scope.Equals(TaskScope.Flow) &&
                !(workflow?.Functions.Any(f => f.Key == function.Key) ?? false))
            {
                return Task.FromResult(Result.Fail(
                    WorkflowErrors.FunctionScopeNotSatisfied(function.Key, function.Scope.Description)));
            }
        }

        return Task.FromResult(Result.Ok());
    }
}
