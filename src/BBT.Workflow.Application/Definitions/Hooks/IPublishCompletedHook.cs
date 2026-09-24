using BBT.Aether.Results;

namespace BBT.Workflow.Definitions;

/// <summary>
/// One unit of work that runs once, after a domain's component deployment has finished publishing
/// every component of a package.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the extension point, not the endpoint.</b> New post-deployment behaviour is a new
/// implementation of this interface registered in DI — never a branch added to
/// <c>DefinitionController</c> or to <see cref="IPublishCompletedAppService"/>. The endpoint exists
/// so that a domain's CD pipeline has exactly one call to make at the end of a deployment; the value
/// of that call grows by adding hooks behind it.
/// </para>
/// <para>
/// <b>A hook MUST be idempotent.</b> The endpoint may legitimately be called twice for one
/// deployment — a retried CD step, an operator repeating it, both the init host and a manual call —
/// and nothing upstream deduplicates.
/// </para>
/// <para>
/// <b>A hook MUST NOT assume it is the only one, or that it runs first.</b> Hooks are ordered by
/// <see cref="Order"/> and every registered hook runs even when an earlier one fails: one failing
/// hook must not silently cancel the rest of a deployment's post-work.
/// </para>
/// <para>
/// <b>The success value is an outcome label, not a message.</b> A hook can succeed in several
/// distinct ways that an operator needs to tell apart — the discovery hook alone answers
/// <c>Refreshed</c>, <c>SkippedNotOwner</c> and <c>Disabled</c>, all of them successes. Returning
/// the label in the <see cref="Result{T}"/> value keeps that distinction without inventing a second
/// success/failure vocabulary: a <c>Fail</c> is a failure, and everything else is a named success.
/// </para>
/// </remarks>
public interface IPublishCompletedHook
{
    /// <summary>
    /// Stable, short identifier reported back to the caller (for example <c>discovery-cache</c>).
    /// </summary>
    /// <remarks>
    /// A CD pipeline reads this to decide which hook failed, so treat it as a contract: renaming it
    /// breaks whatever was keyed on it.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// Execution order, ascending. Ties are broken by <see cref="Name"/> so the sequence stays
    /// deterministic regardless of DI registration order.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Runs the hook.
    /// </summary>
    /// <param name="input">What the caller reported about the finished deployment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>Ok</c> with a short outcome label, or <c>Fail</c> when the work did not happen. Throwing is
    /// allowed but pointless: the pipeline converts an exception into the same failure.
    /// </returns>
    Task<Result<string>> ExecuteAsync(PublishCompletedInput input, CancellationToken cancellationToken);
}
