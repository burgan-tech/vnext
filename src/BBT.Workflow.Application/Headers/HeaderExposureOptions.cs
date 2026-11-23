namespace BBT.Workflow.Headers;

/// <summary>
/// Bound from configuration section "Workflow:HeaderExposure". See <see cref="IHeaderExposureDenyList"/> for where the
/// list applies and where it deliberately does not.
/// </summary>
public sealed class HeaderExposureOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Workflow:HeaderExposure";

    /// <summary>
    /// Header names kept out of transition history and script/mapping contexts, matched exactly and case-insensitively.
    /// Empty (the default) keeps today's behaviour. There is no allow list on purpose: intermediaries add headers a
    /// domain cannot enumerate, so only the sensitive names are listed.
    /// </summary>
    public List<string> Deny { get; set; } = [];
}
