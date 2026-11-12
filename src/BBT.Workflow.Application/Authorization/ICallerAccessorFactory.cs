using BBT.Workflow.Scripting.Caller;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Creates the request-bound <see cref="CallerAccessor"/> that rule-based selection exposes to scripts
/// through <c>ScriptContext.Caller</c>.
/// </summary>
public interface ICallerAccessorFactory
{
    /// <summary>
    /// An accessor for the caller of the current request, resolving on its first read through
    /// <see cref="ICallerRoleResolver.ResolveRoleSetAsync"/> with <paramref name="headers"/>.
    /// </summary>
    /// <param name="headers">The request headers the provider resolves the caller from.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    CallerAccessor ForRequest(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default);
}
