namespace BBT.Workflow.Definitions;

/// <summary>
/// What a CD pipeline reports when it has finished publishing every component of a package.
/// </summary>
/// <remarks>
/// Every field is optional and none of them changes what the hooks do — they are identification for
/// logs and traces, so that "which deployment refreshed this?" has an answer during an incident. The
/// one field with behaviour is <see cref="Domain"/>: when supplied it is checked against the
/// runtime's own domain, so a pipeline pointed at the wrong runtime fails loudly instead of
/// cheerfully refreshing a stranger's caches.
/// </remarks>
public sealed class PublishCompletedInput
{
    /// <summary>
    /// The domain the deployment targeted. Validated against the runtime's domain when present.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// The npm package that was published, for example <c>@burgan-tech/vnext-onboarding</c>.
    /// </summary>
    public string? PackageName { get; set; }

    /// <summary>
    /// The package version that was published, for example <c>1.2.2</c>.
    /// </summary>
    public string? Version { get; set; }
}

/// <summary>
/// One hook's outcome.
/// </summary>
/// <param name="Name">The hook's <see cref="IPublishCompletedHook.Name"/>.</param>
/// <param name="Outcome">A named success (for example <c>Refreshed</c>, <c>Disabled</c>) or <c>Failed</c>.</param>
/// <param name="Message">Detail, present on a failure and on outcomes an operator may need explained.</param>
public sealed record PublishCompletedHookResult(string Name, string Outcome, string? Message = null);

/// <summary>
/// The endpoint's answer: what every hook did.
/// </summary>
/// <param name="Success">False when any hook failed. This is the field a CD pipeline should gate on.</param>
/// <param name="Hooks">One entry per registered hook, in execution order.</param>
/// <remarks>
/// The HTTP status is <c>200</c> even when <see cref="Success"/> is false. A failed hook does not mean
/// the request was bad or that the deployment failed — it means a piece of post-deployment work did
/// not happen, and the caller needs the per-hook detail to decide what that costs. Collapsing that
/// into a non-2xx would throw the detail away and, worse, invite pipelines to treat it as a retry of
/// the whole deployment.
/// </remarks>
public sealed record PublishCompletedOutput(bool Success, IReadOnlyList<PublishCompletedHookResult> Hooks);

/// <summary>
/// Outcome labels the pipeline itself assigns. A hook may return any other label of its own.
/// </summary>
public static class PublishCompletedHookOutcomes
{
    /// <summary>The hook returned a failure, or threw.</summary>
    public const string Failed = "Failed";

    /// <summary>The hook had nothing to do because the feature it serves is switched off.</summary>
    public const string Disabled = "Disabled";
}
