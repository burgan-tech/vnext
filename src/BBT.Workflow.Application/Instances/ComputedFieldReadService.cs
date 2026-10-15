using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Instances;

/// <summary>
/// Scoped implementation of <see cref="IComputedFieldReadService"/>. The master schema comes from the component cache;
/// its computed fields are parsed once per schema definition instance (<see cref="ComputedFieldParser.GetRules"/>) and
/// remembered per reference for the scope, so a list page resolves each schema once.
/// </summary>
public sealed class ComputedFieldReadService(IComponentCacheStore componentCacheStore) : IComputedFieldReadService
{
    private readonly Dictionary<(string Domain, string Flow, string Key, string Version), IReadOnlyList<ComputedFieldRule>> _rules = new();

    /// <inheritdoc />
    public async Task<JsonElement> ApplyAsync(
        Definitions.Workflow? workflow,
        JsonElement exposed,
        CancellationToken cancellationToken = default)
    {
        if (workflow?.Schema is not { } reference || exposed.ValueKind != JsonValueKind.Object)
            return exposed;

        var key = (reference.Domain, reference.Flow, reference.Key, reference.Version);
        if (!_rules.TryGetValue(key, out var rules))
        {
            var schemaResult = await componentCacheStore.GetSchemaAsync(reference, cancellationToken);
            if (!schemaResult.IsSuccess)
                return exposed;

            rules = ComputedFieldParser.GetRules(schemaResult.Value!);
            _rules[key] = rules;
        }

        return ComputedFieldEvaluator.Apply(exposed, rules);
    }
}
