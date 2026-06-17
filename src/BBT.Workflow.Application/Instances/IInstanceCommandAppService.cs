using BBT.Aether.Application;
using BBT.Aether.Results;
using BBT.Workflow.Domain;

namespace BBT.Workflow.Instances;

public interface IInstanceCommandAppService : IApplicationService
{
    Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default);

    Task<Result<TransitionOutput>> TransitionAsync(
        string instance,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a long-poll termination signal for a paused instance and resumes its pipeline.
    /// Role-checked against the entered state's <c>interaction.longPoll.roles</c>. Idempotent: a no-op
    /// when the instance is not awaiting acknowledge (already resumed by acknowledge or fallback).
    /// </summary>
    /// <param name="input">The acknowledge request (domain, workflow, instance, optional version, role).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Ok on success or no-op; Forbidden when the caller's role is not granted; Fail on resume error.</returns>
    Task<Result> AcknowledgeLongPollAsync(
        AcknowledgeLongPollInput input,
        CancellationToken cancellationToken = default);
} 