using BBT.Workflow;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>
/// Pure, dependency-free selection of a default view + candidate list from a <see cref="ViewDefinition"/>.
/// Monitor never evaluates view-selection rules; returns first rule-less entry (or first entry) as default.
/// </summary>
public static class ViewSelector
{
    /// <summary>
    /// Holds the selected default entry and the full candidate list from a <see cref="ViewDefinition"/>.
    /// </summary>
    public readonly record struct Selection(ViewEntry? Default, IReadOnlyList<ViewEntry> Candidates);

    /// <summary>
    /// Selects the default view and returns all candidates from the given definition.
    /// The default is the first rule-less entry, or the first entry if all entries have rules.
    /// Returns empty selection when the definition is null or has no entries.
    /// </summary>
    public static Selection Select(ViewDefinition? definition)
    {
        var entries = definition?.Views ?? [];
        if (entries.Count == 0)
            return new Selection(null, []);

        var def = entries.FirstOrDefault(e => e.Rule is null) ?? entries[0];
        return new Selection(def, entries);
    }
}
