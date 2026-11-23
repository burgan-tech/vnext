using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Authorization;

/// <summary>What <see cref="ISchemaFieldFilterService.PrepareAsync"/> could decide for one caller.</summary>
public enum SchemaExposureOutcome
{
    /// <summary>The workflow has no master schema, or the schema carries no <c>x-roles</c> / <c>x-masking</c> / <c>x-encryption</c> rule.</summary>
    NoRules = 0,

    /// <summary>The caller's plan is decided; <see cref="SchemaExposurePlan.Apply"/> transforms data with it.</summary>
    Ready = 1,

    /// <summary>The workflow names a master schema that could not be read: the rules are unknown.</summary>
    Unavailable = 2
}

/// <summary>
/// One caller's exposure plan over a master schema — which guarded paths it may see and which mask/encryption rules
/// apply to it — decided once (one role resolution, one evaluator) and applied to any number of JSON objects shaped
/// like instance data. Built by <see cref="ISchemaFieldFilterService.PrepareAsync"/>.
/// <para>
/// Unlike <see cref="ISchemaFieldFilterService.ApplyAsync"/>, the outcome keeps "no rules" apart from "rules unknown",
/// so a caller whose fallback is plaintext (transition history) can withhold instead of serving the input as it is.
/// </para>
/// </summary>
public sealed class SchemaExposurePlan
{
    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>(0);
    private static readonly IReadOnlyDictionary<string, FieldMaskRule> NoRules = new Dictionary<string, FieldMaskRule>(0);

    /// <summary>The plan for a workflow without rules.</summary>
    public static readonly SchemaExposurePlan WithoutRules = new(SchemaExposureOutcome.NoRules, NoPaths, NoPaths, NoRules, null);

    /// <summary>The plan for a workflow whose master schema could not be read.</summary>
    public static readonly SchemaExposurePlan Unreadable = new(SchemaExposureOutcome.Unavailable, NoPaths, NoPaths, NoRules, null);

    private readonly IReadOnlySet<string> _pathsWithRoles;
    private readonly IReadOnlySet<string> _visiblePaths;
    private readonly IReadOnlyDictionary<string, FieldMaskRule> _activeMaskRules;
    private readonly IFieldMaskingEngine? _maskingEngine;

    internal SchemaExposurePlan(
        SchemaExposureOutcome outcome,
        IReadOnlySet<string> pathsWithRoles,
        IReadOnlySet<string> visiblePaths,
        IReadOnlyDictionary<string, FieldMaskRule> activeMaskRules,
        IFieldMaskingEngine? maskingEngine)
    {
        Outcome = outcome;
        _pathsWithRoles = pathsWithRoles;
        _visiblePaths = visiblePaths;
        _activeMaskRules = activeMaskRules;
        _maskingEngine = maskingEngine;
    }

    /// <summary>Whether the plan is decided, absent or unknown.</summary>
    public SchemaExposureOutcome Outcome { get; }

    /// <summary>
    /// Prunes the paths the caller may not see and applies the caller's mask/encryption rules to <paramref name="data"/>,
    /// a JSON object. Valid only when <see cref="Outcome"/> is <see cref="SchemaExposureOutcome.Ready"/>.
    /// </summary>
    /// <param name="data">The object to transform; never modified.</param>
    /// <param name="storedTokens">Stored token per opened path, as for <see cref="ISchemaFieldFilterService.ApplyAsync"/>;
    /// without it a non-exempt caller gets a full mask on an <c>encrypt</c> field, never the plaintext.</param>
    public JsonElement Apply(JsonElement data, IReadOnlyDictionary<string, string>? storedTokens = null)
    {
        if (Outcome != SchemaExposureOutcome.Ready)
            throw new InvalidOperationException($"An exposure plan with outcome {Outcome} cannot be applied.");

        return InstanceDataRoleFilter.Apply(data, _pathsWithRoles, _visiblePaths, _activeMaskRules, _maskingEngine, storedTokens);
    }
}
