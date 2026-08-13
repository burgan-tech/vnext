using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Functions;

/// <summary>
/// Default <see cref="IFunctionAccessPolicy"/>: scope enforcement followed by role-grant evaluation.
/// </summary>
public sealed class FunctionAccessPolicy(
    ICurrentUser currentUser,
    ITransitionAuthorizationManager transitionAuthorizationManager) : IFunctionAccessPolicy
{
    /// <inheritdoc />
    public async Task<Result> AuthorizeAsync(
        Function function,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken = default)
    {
        // Scope enforcement. Domain is exempt; Instance/Flow require an instance;
        // Flow additionally requires the function to be declared in the instance's flow.
        if (!function.Scope.Equals(TaskScope.Domain))
        {
            if (instance == null)
                return Result.Fail(
                    WorkflowErrors.FunctionScopeNotSatisfied(function.Key, function.Scope.Description));

            if (function.Scope.Equals(TaskScope.Flow) &&
                !(workflow?.Functions.Any(f => f.Key == function.Key) ?? false))
            {
                return Result.Fail(
                    WorkflowErrors.FunctionScopeNotSatisfied(function.Key, function.Scope.Description));
            }
        }

        // Custom-function authorization: when the function defines Roles, the caller must resolve to an allow.
        // Built-in functions never reach this path (they use their own handlers/authorization).
        if (function.Roles.Count > 0)
        {
            // Honor the legacy `role` header: a caller whose roles arrive only as a header would
            // otherwise be treated as role-less and rejected with 403 by an allowlist grant set.
            var allowed = await transitionAuthorizationManager.IsAnyRoleAllowedForGrantsAsync(
                currentUser.ResolveCallerRoles(headers),
                function.Roles,
                instance,
                new AuthorizationRequestContext(headers, queryParameters),
                cancellationToken);

            if (!allowed)
                return Result.Fail(WorkflowErrors.FunctionAccessDenied(function.Key));
        }

        return Result.Ok();
    }
}
