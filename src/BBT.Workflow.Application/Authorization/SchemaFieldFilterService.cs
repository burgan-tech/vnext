using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Builds and applies the caller's EXPOSURE PLAN for instance data from the workflow's master schema:
/// <c>x-roles</c> decides which properties the caller may see, and <c>x-masking</c> / <c>x-encryption</c>
/// (type <c>hash</c> or <c>encrypt</c>) decide how each visible property is transformed — for <c>encrypt</c>, an
/// exempt caller reads the plaintext and everyone else the stored token. Both are decided with ONE role resolution and ONE evaluator, and
/// written in ONE pass, so the hide and mask decisions always describe the same caller.
/// <para>
/// Order: <c>x-roles</c> → <c>x-masking</c> → <c>x-encryption</c>. A property hidden by <c>x-roles</c> is
/// pruned and never reaches a transform; a property carries at most one transform (enforced at publish).
/// </para>
/// <para>
/// <c>x-masking.roles</c> and <c>x-encryption.roles</c> are allow-only exemption lists: the rule applies unless the caller
/// matches an allow grant. Role resolution failure, an empty role set and an unlisted role all leave the
/// rule applied — the same fail-closed direction <c>x-roles</c> takes when it prunes every guarded field.
/// </para>
/// Extracted from InstanceQueryAppService to allow reuse in command and query paths.
/// </summary>
public sealed class SchemaFieldFilterService(
    IComponentCacheStore componentCacheStore,
    ITransitionAuthorizationManager transitionAuthorizationManager,
    ICallerRoleResolver callerRoleResolver,
    IFieldMaskingEngine maskingEngine,
    IOptions<SchemaMaskingOptions> maskingOptions,
    IOptions<SchemaEncryptionOptions>? encryptionOptions = null) : ISchemaFieldFilterService, IListSchemaFieldFilterFactory
{
    private static readonly IReadOnlyDictionary<string, FieldMaskRule> NoMaskRules =
        new Dictionary<string, FieldMaskRule>(0);

    private Dictionary<(string Domain, string Flow, string Key, string Version), SchemaExposureMetadata>? _listMetadata;

    /// <inheritdoc />
    public ISchemaFieldFilterService CreateForList() => new SchemaFieldFilterService(
        componentCacheStore, transitionAuthorizationManager, callerRoleResolver, maskingEngine, maskingOptions, encryptionOptions)
    {
        _listMetadata = new()
    };

    /// <inheritdoc />
    public async Task<JsonElement?> ApplyAsync(
        Definitions.Workflow? workflow,
        JsonElement? data,
        Instance? instance = null,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? storedTokens = null)
    {
        if (workflow?.Schema is null || !data.HasValue)
            return data;
        var element = data.GetValueOrDefault();
        if (element.ValueKind != JsonValueKind.Object)
            return data;

        var reference = workflow.Schema;
        var key = (reference.Domain, reference.Flow, reference.Key, reference.Version);
        SchemaExposureMetadata? metadata = null;
        if (_listMetadata?.TryGetValue(key, out metadata) != true)
        {
            var schemaResult = await componentCacheStore.GetSchemaAsync(reference, cancellationToken);
            if (!schemaResult.IsSuccess) return data;
            metadata = SchemaRolesParser.ParseExposure(schemaResult.Value!.Schema);
            _listMetadata?.Add(key, metadata);
        }

        var pathRoleGrants = metadata!.PathRoleGrants;
        var maskRules = SelectEnabledRules(metadata.PathMaskRules, maskingOptions.Value.Enabled);
        if (pathRoleGrants.Count == 0 && maskRules.Count == 0)
            return data;

        // The role set must match how the surrounding read was authorized and cache-keyed, otherwise
        // the same cache entry can be filled with differently-filtered bodies — hence the shared resolver
        // rather than a local read of the current user.
        var callerRolesResult = await callerRoleResolver.ResolveRolesAsync(
            requestContext?.Headers, cancellationToken);

        var pathsWithRoles = new HashSet<string>(pathRoleGrants.Keys, StringComparer.Ordinal);

        // A provider that fails (neither built-in one does — morph-idm resolves its failures to an empty
        // set, evaluated normally below) prunes every guarded field AND applies every mask rule. This method
        // has no failure channel, and the alternative — serving the data unfiltered — would leak exactly
        // the fields the schema guards.
        if (!callerRolesResult.IsSuccess)
            return InstanceDataRoleFilter.Apply(
                element, pathsWithRoles, new HashSet<string>(0), maskRules, maskingEngine, storedTokens);

        // One evaluator for the whole schema: the union of every guarded path's grants AND every exemption
        // list decides the single prefetch, so a $PreviousUser exemption can match.
        var evaluator = await transitionAuthorizationManager.CreateEvaluatorAsync(
            instance,
            workflow,
            requestContext,
            pathRoleGrants.SelectMany(kv => kv.Value).Concat(maskRules.Values.SelectMany(r => r.ExemptRoles)),
            cancellationToken);

        var callerRoles = callerRolesResult.Value;
        var visiblePaths = pathRoleGrants.Count == 0
            ? new HashSet<string>(0)
            : SchemaFieldVisibilityService.GetVisiblePaths(pathRoleGrants, callerRoles, evaluator);

        return InstanceDataRoleFilter.Apply(
            element, pathsWithRoles, visiblePaths, ResolveActiveMaskRules(maskRules, callerRoles, evaluator), maskingEngine, storedTokens);
    }

    /// <summary>
    /// Honours <c>SchemaMasking:Enabled</c> for <c>x-masking</c> rules. <c>x-encryption</c> rules are never switched off
    /// on the read path: <c>hash</c> is already applied in the stored value, and dropping an <c>encrypt</c> rule would
    /// serve the engine's plaintext to every caller.
    /// </summary>
    private static IReadOnlyDictionary<string, FieldMaskRule> SelectEnabledRules(
        IReadOnlyDictionary<string, FieldMaskRule> rules, bool maskingEnabled)
    {
        if (rules.Count == 0 || maskingEnabled)
            return rules;

        var selected = new Dictionary<string, FieldMaskRule>(rules.Count, StringComparer.Ordinal);
        foreach (var (path, rule) in rules)
        {
            if (rule.IsEncryption)
                selected[path] = rule;
        }

        return selected;
    }

    /// <summary>
    /// The mask rules that apply to this caller: every rule, minus those whose allow-only exemption list the caller
    /// matches. <see cref="IRoleGrantEvaluator.IsAnyRoleAllowed"/> over an allow-only list is true exactly when one allow
    /// grant matches (predefined and dynamic grants included), so a role-less, unlisted, misspelled or unresolvable caller
    /// keeps the transform — fail closed. An empty list exempts nobody (guarded here: the canonical rule would read it as
    /// "no restriction").
    /// </summary>
    internal static IReadOnlyDictionary<string, FieldMaskRule> ResolveActiveMaskRules(
        IReadOnlyDictionary<string, FieldMaskRule> maskRules,
        IReadOnlyCollection<string>? callerRoles,
        IRoleGrantEvaluator evaluator)
    {
        if (maskRules.Count == 0)
            return NoMaskRules;

        // A caller without roles is evaluated with an empty role name; it may still match an identity-bound grant
        // ($InstanceStarter, $user.…) but never a role-bound one — a $role. value resolving to "" must not exempt it.
        var roleCount = callerRoles?.Count(r => !string.IsNullOrWhiteSpace(r)) ?? 0;

        Dictionary<string, FieldMaskRule>? active = null;
        foreach (var (path, rule) in maskRules)
        {
            var exempt = roleCount > 0
                ? rule.ExemptRoles
                : rule.ExemptRoles.Where(g => !TransitionAuthorizationManager.IsUnprovableRoleBoundGrant(g, 0)).ToList();
            if (exempt.Count > 0 && evaluator.IsAnyRoleAllowed(callerRoles, exempt))
                continue;

            (active ??= new Dictionary<string, FieldMaskRule>(maskRules.Count, StringComparer.Ordinal))[path] = rule;
        }

        return active ?? NoMaskRules;
    }
}
