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
        if (function.InputSchema is not null &&
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
    /// Validates that contract references point at the component type they are meant to describe.
    /// </summary>
    private static void ValidateContractReferences(Function function, ComponentValidationResult result)
    {
        ValidateReferenceFlow(function.InputSchema, RuntimeSysSchemaInfo.Schemas, nameof(Function.InputSchema), result);
        ValidateReferenceFlow(function.OutputSchema, RuntimeSysSchemaInfo.Schemas, nameof(Function.OutputSchema), result);
        ValidateReferenceFlow(function.InputView, RuntimeSysSchemaInfo.Views, nameof(Function.InputView), result);
        ValidateReferenceFlow(function.OutputView, RuntimeSysSchemaInfo.Views, nameof(Function.OutputView), result);
    }

    private static void ValidateReferenceFlow(
        Reference? reference,
        string expectedFlow,
        string propertyName,
        ComponentValidationResult result)
    {
        if (reference is null)
            return;

        var member = $"{nameof(Function)}.{propertyName}";

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
}
