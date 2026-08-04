using System.Text.Json;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow function components (sys-functions).
/// Ensures function definitions are properly structured and contain required fields.
/// </summary>
public sealed class FunctionComponentValidator : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Functions;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();

        try
        {
            var function = attributes.Deserialize<Function>(JsonSerializerConstants.JsonOptions);
            if (function == null)
            {
                result.AddError("Failed to deserialize function from attributes.", nameof(Function));
                return result;
            }

            // Validate required task field
            if (function.Task == null && !function.OnExecutionTasks.Any())
            {
                result.AddError("Function task or onExecutionTask is required.", $"{nameof(Function)}.{nameof(Function.Task)}");
            }

            if (function.OnExecutionTasks.Any())
            {
                if (function.Output == null)
                {
                    result.AddError("Function output is required.", $"{nameof(Function)}.{nameof(Function.Output)}");
                }
            }

            // Validate scope
            if (function.Scope == default)
            {
                result.AddError("Function scope is required.", $"{nameof(Function)}.{nameof(Function.Scope)}");
            }

            ValidateVerbs(function, result);
            ValidateContractReferences(function, result);

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for function: {ex.Message}", nameof(Function));
            return result;
        }
    }

    /// <summary>
    /// Validates declared HTTP verbs. An empty list is valid and means "no restriction", so only the
    /// contents of a non-empty declaration are checked.
    /// </summary>
    private static void ValidateVerbs(Function function, ComponentValidationResult result)
    {
        const string member = $"{nameof(Function)}.{nameof(Function.Verbs)}";

        foreach (var verb in function.Verbs.Where(v => !FunctionVerb.IsKnown(v)))
        {
            result.AddError(
                $"Function verb '{verb}' is not supported. Supported verbs: {string.Join(", ", FunctionVerb.All)}.",
                member);
        }

        // An input schema describes the request body, so a function that only ever answers bodyless
        // verbs can never apply it - the declaration would be silently dead.
        if (function.HasInputSchema &&
            function.Verbs.Count > 0 &&
            function.Verbs.All(v => !FunctionVerb.CarriesBody(v)))
        {
            result.AddError(
                $"Function declares inputSchema but none of its verbs ({string.Join(", ", function.Verbs)}) " +
                "carry a request body, so the schema would never be applied. Declare a body-carrying " +
                $"verb ({FunctionVerb.Post}, {FunctionVerb.Patch} or {FunctionVerb.Delete}) or remove inputSchema.",
                $"{nameof(Function)}.{nameof(Function.InputSchema)}");
        }
    }

    /// <summary>
    /// Validates every entry of every contract slot: the reference points at the component type the
    /// slot is meant to describe, and the rule-based entries form a reachable chain.
    /// </summary>
    private static void ValidateContractReferences(Function function, ComponentValidationResult result)
    {
        ValidateSlot(
            function.InputSchema?.Schemas.Select(e => new SlotEntry(e.Rule is not null, e.Schema)).ToList(),
            RuntimeSysSchemaInfo.Schemas, nameof(Function.InputSchema), allowsViewOptions: false, result);

        ValidateSlot(
            function.OutputSchema?.Schemas.Select(e => new SlotEntry(e.Rule is not null, e.Schema)).ToList(),
            RuntimeSysSchemaInfo.Schemas, nameof(Function.OutputSchema), allowsViewOptions: false, result);

        ValidateSlot(
            function.InputView?.Views
                .Select(e => new SlotEntry(e.Rule is not null, e.View, e.Extensions is { Length: > 0 })).ToList(),
            RuntimeSysSchemaInfo.Views, nameof(Function.InputView), allowsViewOptions: true, result);

        ValidateSlot(
            function.OutputView?.Views
                .Select(e => new SlotEntry(e.Rule is not null, e.View, e.Extensions is { Length: > 0 })).ToList(),
            RuntimeSysSchemaInfo.Views, nameof(Function.OutputView), allowsViewOptions: true, result);
    }

    /// <summary>
    /// Validates one contract slot. Entries are evaluated in declaration order at runtime and the
    /// first match wins, so a rule-less entry short-circuits everything after it.
    /// </summary>
    private static void ValidateSlot(
        List<SlotEntry>? entries,
        string expectedFlow,
        string propertyName,
        bool allowsViewOptions,
        ComponentValidationResult result)
    {
        if (entries is null || entries.Count == 0)
            return;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var member = entries.Count == 1
                ? $"{nameof(Function)}.{propertyName}"
                : $"{nameof(Function)}.{propertyName}[{index}]";

            ValidateReferenceFlow(entry.Reference, expectedFlow, propertyName, member, result);

            // A rule-less entry always matches, so nothing declared after it can ever be reached.
            if (!entry.HasRule && index < entries.Count - 1)
            {
                result.AddError(
                    $"Function {propertyName} entry {index} declares no rule, so it always matches and the " +
                    $"{entries.Count - index - 1} entry(s) after it are unreachable. Move the rule-less " +
                    "fallback entry to the end.",
                    member);
            }

            // 'extensions' loads instance data alongside a state view; a function has no data function
            // to apply them to, so honoring them is impossible and ignoring them silently is worse.
            if (allowsViewOptions && entry.HasExtensions)
            {
                result.AddError(
                    $"Function {propertyName} entry {index} declares 'extensions', which only applies to " +
                    "state and transition views. Remove it.",
                    member);
            }
        }
    }

    private static void ValidateReferenceFlow(
        Reference? reference,
        string expectedFlow,
        string propertyName,
        string member,
        ComponentValidationResult result)
    {
        if (reference is null)
        {
            result.AddError($"Function {propertyName} entry requires a reference.", member);
            return;
        }

        if (string.IsNullOrWhiteSpace(reference.Key))
            result.AddError($"Function {propertyName} reference requires a key.", member);

        if (string.IsNullOrWhiteSpace(reference.Domain))
            result.AddError($"Function {propertyName} reference requires a domain.", member);

        if (string.IsNullOrWhiteSpace(reference.Version))
            result.AddError($"Function {propertyName} reference requires a version.", member);

        if (!string.Equals(reference.Flow, expectedFlow, StringComparison.OrdinalIgnoreCase))
        {
            result.AddError(
                $"Function {propertyName} must reference the '{expectedFlow}' flow but references '{reference.Flow}'.",
                member);
        }
    }

    /// <summary>Uniform projection of a view or schema entry so both slot families share one check.</summary>
    private sealed record SlotEntry(bool HasRule, Reference? Reference, bool HasExtensions = false);
}
