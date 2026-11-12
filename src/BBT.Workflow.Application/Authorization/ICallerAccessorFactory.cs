using BBT.Workflow.Shared;
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

    /// <summary>
    /// The accessor for a transition hop: the hop's caller when it was driven by one
    /// (<see cref="ExecutionActor.User"/> — the request itself, the async-accept job replaying the
    /// caller's headers, and the automatic hops chained inline after them); otherwise
    /// <see cref="NullCallerAccessor"/> (<see cref="ExecutionActor.System"/> hops such as a fired
    /// timer, a SubFlow resume or a continuation have no caller, and an empty set would read as "holds no role").
    /// </summary>
    ICallerAccessor ForHop(ExecutionActor actor, IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default);
}
