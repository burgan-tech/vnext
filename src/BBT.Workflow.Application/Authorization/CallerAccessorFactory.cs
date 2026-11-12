using BBT.Workflow.Logging;
using BBT.Workflow.Scripting.Caller;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Builds request-bound <see cref="CallerAccessor"/>s over the configured <see cref="ICallerRoleResolver"/>.
/// </summary>
/// <remarks>
/// Scoped, like the resolver: morph-idm memoizes its answer per request scope and credential, so every
/// accessor of a request — and every other surface that resolves roles in it (authorization, caller-scoped
/// cache keys) — shares one provider call and one role set. <see cref="ICallerRoleResolver.ResolveRoleSetAsync"/>
/// is used rather than <c>ResolveRolesAsync</c> because only it reports <see cref="CallerRoleOutcome.Failed"/>;
/// the latter answers an empty set on a provider failure, which a selection rule would read as "holds no role".
/// </remarks>
public sealed class CallerAccessorFactory(
    ICallerRoleResolver callerRoleResolver,
    ILogger<CallerAccessorFactory> logger) : ICallerAccessorFactory
{
    /// <inheritdoc />
    public CallerAccessor ForRequest(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default)
    {
        var headerCopy = headers?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return new CallerAccessor(async () =>
        {
            var resolution = await callerRoleResolver.ResolveRoleSetAsync(headerCopy, cancellationToken);
            var failed = resolution.Outcome == CallerRoleOutcome.Failed;
            if (failed)
                logger.CallerRolesUnavailableForSelection(resolution.FailureKind);

            return new CallerRoleSnapshot(
                failed ? [] : resolution.Roles,
                failed,
                resolution.Outcome == CallerRoleOutcome.FromHeader);
        });
    }
}
