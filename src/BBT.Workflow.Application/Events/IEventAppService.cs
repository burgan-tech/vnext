using BBT.Aether.Application;
using BBT.Aether.Results;

namespace BBT.Workflow.Events;

/// <summary>
/// Handles inbound external events: resolves the event mapping declared on the workflow (start) or
/// transition (transition), compiles and runs it to obtain a correlation key + body, then either starts
/// a new instance or advances the correlated active instance.
/// </summary>
public interface IEventAppService : IApplicationService
{
    /// <summary>
    /// Processes a single inbound event. Returns success (with the start/transition output, or null when an
    /// event-transition is intentionally ignored because no active instance matches the correlation key).
    /// </summary>
    Task<Result<object?>> HandleAsync(EventInput input, CancellationToken cancellationToken = default);
}