using System.Text.Json;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Processes component validation operations by delegating to appropriate validators.
/// This class acts as a coordinator that selects the correct validator based on the component type
/// and orchestrates the validation operation.
/// </summary>
/// <param name="validators">A collection of component validators available for validation.</param>
public sealed class ComponentValidatorProcessor(IEnumerable<IComponentValidator> validators)
{
    /// <summary>
    /// Validates a component by finding the appropriate validator and executing the validation.
    /// </summary>
    /// <param name="componentType">The component type identifier that determines which validator to use.</param>
    /// <param name="attributes">The JSON element containing the component attributes to be validated.</param>
    /// <returns>A <see cref="ComponentValidationResult"/> containing any validation errors.</returns>
    /// <exception cref="NotSupportedException">Thrown when no validator is found for the specified component type.</exception>
    public ComponentValidationResult Validate(string componentType, JsonElement attributes)
    {
        var validator = validators.FirstOrDefault(v => v.CanHandle(componentType));
        if (validator == null)
            throw new NotSupportedException($"No validator found for component type '{componentType}'.");

        return Invoke(validator, componentType, attributes);
    }

    /// <summary>
    /// Attempts to validate a component, returning false if no validator is found.
    /// </summary>
    /// <param name="componentType">The component type identifier that determines which validator to use.</param>
    /// <param name="attributes">The JSON element containing the component attributes to be validated.</param>
    /// <param name="result">When this method returns, contains the validation result if a validator was found.</param>
    /// <returns>True if a validator was found and validation was performed; otherwise, false.</returns>
    public bool TryValidate(string componentType, JsonElement attributes, out ComponentValidationResult result)
    {
        var validator = validators.FirstOrDefault(v => v.CanHandle(componentType));
        if (validator == null)
        {
            result = ComponentValidationResult.Success();
            return false;
        }

        result = Invoke(validator, componentType, attributes);
        return true;
    }

    /// <summary>
    /// Runs one validator, turning an authoring error raised while the definition is being
    /// MATERIALISED into a validation error instead of letting it escape as an unhandled exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every validator materialises the component from its JSON before it can inspect it
    /// (<c>attributes.Deserialize&lt;WorkflowTask&gt;()</c>, <c>…&lt;Workflow&gt;()</c>, …), and a
    /// definition's own <c>Configure</c> reports a bad shape by throwing: <c>FanOutTask</c> rejects
    /// the reserved <c>mode: "durable"</c>, an <c>itemsPath</c> that is not <c>$.</c>-rooted, a
    /// <c>maxDegreeOfParallelism</c> below 1; <c>HttpTask</c> rejects a missing <c>url</c>;
    /// <c>SubProcessTask</c> and <c>GetInstancesTask</c> reject a missing trigger domain/flow. Only
    /// <see cref="JsonException"/> was ever caught, so every one of those authoring mistakes reached
    /// the publish endpoint's generic handler and came back as an opaque HTTP 500 — with the
    /// exception's message, which already names the offending field AND the supported values,
    /// discarded. The author was told only that something broke on the server.
    /// </para>
    /// <para>
    /// Caught HERE, around the single validator invocation, rather than around the publish operation:
    /// the blast radius is one validator call whose entire job is to materialise a definition and
    /// look at it, so an <see cref="ArgumentException"/> raised inside it is by construction about
    /// the definition's shape. Everything else — a database failure, a cache failure, the
    /// <see cref="NotSupportedException"/> above — is untouched and still surfaces as a 500, which is
    /// what a genuine infrastructure fault must stay.
    /// </para>
    /// <para>
    /// <see cref="ArgumentNullException"/> and <see cref="ArgumentOutOfRangeException"/> are included
    /// deliberately, being <see cref="ArgumentException"/> subtypes: <c>HttpTask.Configure</c> reports
    /// its missing <c>url</c> as the former, and an author cannot tell the two apart from the outside.
    /// </para>
    /// </remarks>
    private static ComponentValidationResult Invoke(
        IComponentValidator validator,
        string componentType,
        JsonElement attributes)
    {
        try
        {
            return validator.Validate(attributes);
        }
        catch (ArgumentException ex)
        {
            var result = new ComponentValidationResult();

            // Keyed by component type + the rejected parameter ('config' for a task's own
            // Configure), so the errors dictionary locates the mistake the way a flow's
            // 'workflow.ErrorBoundary.OnError[0].Transition' does rather than reporting a bare
            // message with no field.
            result.AddError(ex.Message, $"{componentType}.{ex.ParamName ?? DefaultMemberName}");
            return result;
        }
    }

    /// <summary>
    /// Member name used when an authoring exception names no parameter — the component's attributes
    /// as a whole are then the only thing that can be pointed at.
    /// </summary>
    private const string DefaultMemberName = "attributes";
}
