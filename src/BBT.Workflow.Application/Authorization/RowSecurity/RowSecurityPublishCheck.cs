using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Publish-time checks of <c>attributes.rowSecurity</c> that need the component key or the master schema
/// (vnext#1107), run by the definition publish path after the schema-blind
/// <see cref="RowSecurityDefinitionRules"/> (which <c>WorkflowValidator</c> runs synchronously and without
/// either input).
/// </summary>
/// <remarks>
/// <para>Errors: RS-E16 (<c>sys-*</c> flow), and, against the master schema, RS-E08 / E14 / E15 / E18 as
/// classified by <see cref="RowSecurityProtectedPaths.Classify"/>. Warnings: RS-W1 (field not in the
/// schema), RS-W2a (<c>in</c> / range / pattern on a field without <c>x-indexed</c>), RS-W2c (<c>ne</c> /
/// <c>nin</c> as the rule's only predicates), RS-W5 (<c>not</c> / <c>nin</c> on a non-<c>required</c> field),
/// RS-W6r (<c>Headers</c> value on an <c>x-roles</c> path), RS-W7 (SubFlow children do not inherit row
/// security). <c>eq</c> never warns.</para>
/// <para>Schema not resolvable at publish (the publish order of <c>wf sync</c> is not guaranteed): the
/// schema-aware checks are downgraded to one RS-W0 warning and logged; the read-time planner applies the
/// same <see cref="RowSecurityProtectedPaths"/> refusals and drops the rule (fail closed).</para>
/// <para>Scoped; stateless apart from its dependencies. Messages never echo authored values.</para>
/// </remarks>
public sealed class RowSecurityPublishCheck(
    IComponentCacheStore componentCacheStore,
    ILogger<RowSecurityPublishCheck> logger)
{
    private const string Root = "rowSecurity";
    private const string SysPrefix = "sys-";
    private const string WarningW0 = "RS-W0";
    private const string WarningW1 = "RS-W1";
    private const string WarningW2a = "RS-W2a";
    private const string WarningW2c = "RS-W2c";
    private const string WarningW5 = "RS-W5";
    private const string WarningW7 = "RS-W7";

    private static readonly string[] UnindexedSensitiveOperators =
        ["in", "gt", "ge", "lt", "le", "between", "like", "match", "startswith", "endswith"];

    private static readonly string[] NegativeOperators = ["ne", "nin"];

    /// <summary>
    /// Checks the row security of <paramref name="workflow"/> as published under <paramref name="componentKey"/>.
    /// </summary>
    /// <param name="componentKey">The flow's component key.</param>
    /// <param name="workflow">The deserialized flow definition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Every finding; empty when the flow declares no <c>rowSecurity</c>.</returns>
    public async Task<IReadOnlyList<RowSecurityFinding>> CheckAsync(
        string componentKey, Definitions.Workflow workflow, CancellationToken ct)
    {
        var rowSecurity = workflow.RowSecurity;
        if (rowSecurity is null)
            return [];

        var findings = new List<RowSecurityFinding>();

        if (componentKey.StartsWith(SysPrefix, StringComparison.Ordinal))
            findings.Add(Error(RowSecurityErrorCodes.E16, Root, "rowSecurity cannot be declared on a sys-* flow."));

        foreach (var state in workflow.States.Where(s => s.SubFlow is not null))
        {
            findings.Add(Warning(WarningW7, Root, "rowSecurity is not inherited by SubFlow children."));
            logger.RowSecuritySubFlowNotInherited(componentKey, state.Key);
        }

        var templates = ParseRuleFilters(rowSecurity);
        if (templates.Count == 0 || workflow.Schema is null)
            return findings;

        var schema = await ResolveSchemaAsync(workflow.Schema, ct);
        if (schema is null)
        {
            findings.Add(Warning(WarningW0, Root,
                "The master schema could not be resolved at publish; schema-aware row-security checks were skipped and rules on protected paths are dropped at read time."));
            logger.RowSecurityPublishSchemaChecksSkipped(componentKey, workflow.Schema.Key);
            return findings;
        }

        foreach (var (index, template) in templates)
            CheckRule(template, $"{Root}.rules[{index}]", schema, findings);

        return findings;
    }

    private static List<(int Index, RowFilterTemplate Template)> ParseRuleFilters(RowSecurityDefinition rowSecurity)
    {
        var templates = new List<(int, RowFilterTemplate)>();
        if (rowSecurity.Rules is null)
            return templates;

        for (var i = 0; i < rowSecurity.Rules.Count; i++)
        {
            // A rule without a filter has no leaf to check; a filter that does not parse is reported by
            // RowSecurityDefinitionRules (WorkflowValidator).
            if (rowSecurity.Rules[i]?.Filter is not { ValueKind: JsonValueKind.Object } filter)
                continue;

            var parsed = RowFilterTemplateParser.Parse(filter);
            if (parsed.IsSuccess)
                templates.Add((i, parsed.Value!));
        }

        return templates;
    }

    private async Task<SchemaView?> ResolveSchemaAsync(Reference reference, CancellationToken ct)
    {
        var result = await componentCacheStore.GetSchemaAsync(reference, ct);
        if (!result.IsSuccess || result.Value is null)
            return null;

        var root = result.Value.Schema;
        try
        {
            return new SchemaView(
                SchemaRolesParser.ParseExposure(root),
                SchemaFilterMetadataResolver.Resolve(root),
                RequiredPaths(root));
        }
        catch (ArgumentException)
        {
            // A schema the publish validator would have refused; treat it as unresolvable (read time fails closed).
            return null;
        }
    }

    private static void CheckRule(RowFilterTemplate template, string rulePath, SchemaView schema,
        List<RowSecurityFinding> findings)
    {
        var leaves = new List<(RowTemplateLeaf Leaf, bool Negated)>();
        CollectLeaves(template.Root, negated: false, leaves);

        foreach (var (leaf, negated) in leaves)
            CheckLeaf(leaf, negated, $"{rulePath}.filter.{leaf.Field}.{leaf.Operator}", schema, findings);

        if (leaves.Count > 0 && leaves.All(l => NegativeOperators.Contains(l.Leaf.Operator)))
            findings.Add(Warning(WarningW2c, rulePath,
                "ne / nin as the only predicates of a rule cannot use an index and match most rows."));
    }

    private static void CheckLeaf(RowTemplateLeaf leaf, bool negated, string path, SchemaView schema,
        List<RowSecurityFinding> findings)
    {
        if (leaf.IsInstanceColumn)
            return;

        var protection = Sources(leaf.Value)
            .Select(source => RowSecurityProtectedPaths.Classify(leaf.Field, schema.Exposure, source))
            .DefaultIfEmpty(RowSecurityPathProtection.None)
            .Max();

        if (protection != RowSecurityPathProtection.None)
        {
            var code = RowSecurityProtectedPaths.CodeOf(protection)!;
            findings.Add(RowSecurityProtectedPaths.IsRefused(protection)
                ? Error(code, path, ProtectionMessage(protection))
                : Warning(code, path, ProtectionMessage(protection)));
        }

        var metadata = schema.Fields?.GetFieldMetadata(leaf.Field);
        if (metadata is null)
            findings.Add(Warning(WarningW1, path, "The filtered field is not declared in the master schema."));
        else if (UnindexedSensitiveOperators.Contains(leaf.Operator) && !metadata.Indexed)
            findings.Add(Warning(WarningW2a, path,
                "in / range / pattern operators on a field without x-indexed may scan large flows."));

        if ((negated || leaf.Operator == "nin") && !schema.Required.Contains(leaf.Field))
            findings.Add(Warning(WarningW5, path,
                "not / nin on a field that is not required also matches rows where the field is missing."));
    }

    private static string ProtectionMessage(RowSecurityPathProtection protection) => protection switch
    {
        RowSecurityPathProtection.Encrypted =>
            "A row-security filter cannot use an x-encryption: encrypt path or one of its ancestors or descendants.",
        RowSecurityPathProtection.Hashed => "A row-security filter cannot use an x-encryption: hash path.",
        RowSecurityPathProtection.Masked => "A row-security filter cannot use an x-masking path.",
        RowSecurityPathProtection.QueryOnRoleRestricted =>
            "A QueryParameters value cannot be compared with an x-roles path: the caller could probe a hidden field.",
        _ => "A Headers value compared with an x-roles path lets the caller probe the hidden field if the header is client-controlled."
    };

    private static void CollectLeaves(RowTemplateNode node, bool negated, List<(RowTemplateLeaf, bool)> leaves)
    {
        switch (node)
        {
            case RowTemplateAnd and:
                foreach (var child in and.Children)
                    CollectLeaves(child, negated, leaves);
                break;
            case RowTemplateOr or:
                foreach (var child in or.Children)
                    CollectLeaves(child, negated, leaves);
                break;
            case RowTemplateNot not:
                CollectLeaves(not.Child, negated: true, leaves);
                break;
            case RowTemplateLeaf leaf:
                leaves.Add((leaf, negated));
                break;
        }
    }

    /// <summary>The value sources of a leaf: null for a literal, else the parameter's source.</summary>
    private static IEnumerable<RowParameterSource?> Sources(RowTemplateValue value) => value switch
    {
        RowTemplateValue.Elements elements => elements.Items.SelectMany(Sources),
        RowTemplateValue.Parameter parameter => [parameter.Path.Source],
        RowTemplateValue.SplitParameter split => [split.Path.Source],
        _ => [null]
    };

    /// <summary>Dot-separated paths that are required at every level from the root.</summary>
    private static HashSet<string> RequiredPaths(JsonElement root)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        CollectRequired(root, string.Empty, paths);
        return paths;
    }

    private static void CollectRequired(JsonElement node, string prefix, HashSet<string> paths)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
            return;

        node.TryGetProperty("properties", out var properties);
        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
                continue;

            var name = item.GetString()!;
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            paths.Add(path);

            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var child))
                CollectRequired(child, path, paths);
        }
    }

    private static RowSecurityFinding Error(string code, string path, string message) =>
        new(RowSecurityFindingSeverity.Error, code, path, message);

    private static RowSecurityFinding Warning(string code, string path, string message) =>
        new(RowSecurityFindingSeverity.Warning, code, path, message);

    private sealed record SchemaView(
        SchemaExposureMetadata Exposure,
        SchemaFilterContext? Fields,
        IReadOnlySet<string> Required);
}
