using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared.Json;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Sandbox;
using InputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Rules.ExpressoInstanceView, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext, object>;
using OutputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Rules.ExpressoInstanceView, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, object>;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Publish-time validation of the declarative <c>input</c>/<c>output</c> blocks of task bindings
/// (<see cref="OnExecuteTask"/>) wherever a component holds them: flow states (<c>onEntries</c>,
/// <c>onExits</c>), state / start / shared / well-known (<c>cancel</c>, <c>updateData</c>, <c>exit</c>)
/// transitions (<c>onExecutionTasks</c>), function (<c>task</c>, <c>onExecutionTasks</c>) and extension
/// (<c>task</c>) definitions.
/// </summary>
/// <remarks>
/// Every path is parsed with <see cref="JsonPath"/> and every <c>set</c> expression is compiled with the
/// phase's <see cref="ExpressionSignatures.Mapping"/> signature through the shared singleton compiler, so
/// publish warms the compile cache; compile is validation only, nothing is stored. Errors read
/// <c>{jsonLocation}: {message}</c>, for example
/// <c>states[review].onEntries[0].output.set['$.kyc.score']: ...</c>. A binding with neither a mapping nor
/// blocks stays valid. Whether a task TYPE supports an input surface is not checked here.
/// </remarks>
public sealed class MappingBlockValidator(IExpressionCompiler compiler)
{
    private static readonly string[] InputSurfaces = ["body", "headers", "query", "route"];

    private static readonly string[] InputVariables =
    [
        ExpressionScope.DataVariable, ExpressionScope.InputVariable, ExpressionScope.InstanceVariable,
        ExpressionScope.NowVariable, ExpressionScope.UserVariable, ExpressionScope.ContextVariable
    ];

    /// <summary>Whether <paramref name="componentType"/> can hold task bindings this validator checks.</summary>
    public static bool AppliesTo(string componentType) =>
        componentType is RuntimeSysSchemaInfo.Flows or RuntimeSysSchemaInfo.Functions or RuntimeSysSchemaInfo.Extensions;

    /// <summary>
    /// Validates every binding of a flow, function or extension component and appends the errors to
    /// <paramref name="result"/>. A definition that does not deserialize is skipped: the component's own
    /// validator already reported it.
    /// </summary>
    public void Validate(string componentType, JsonElement attributes, ComponentValidationResult result)
    {
        try
        {
            switch (componentType)
            {
                case RuntimeSysSchemaInfo.Flows:
                    if (attributes.Deserialize<Workflow>(JsonSerializerConstants.JsonOptions) is { } workflow)
                        ValidateWorkflow(workflow, result.ValidationErrors);
                    break;
                case RuntimeSysSchemaInfo.Functions:
                    if (attributes.Deserialize<Function>(JsonSerializerConstants.JsonOptions) is { } function)
                        ValidateFunction(function, result.ValidationErrors);
                    break;
                case RuntimeSysSchemaInfo.Extensions:
                    if (attributes.Deserialize<Extension>(JsonSerializerConstants.JsonOptions) is { } extension
                        && extension.Task is not null)
                        ValidateBinding(extension.Task, "task", result.ValidationErrors);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Reported by the component's own validator.
        }
    }

    private void ValidateWorkflow(Workflow workflow, IList<ValidationResult> errors)
    {
        ValidateTransition(workflow.StartTransition, "startTransition", errors);
        ValidateTransition(workflow.Cancel, "cancel", errors);
        ValidateTransition(workflow.UpdateData, "updateData", errors);
        ValidateTransition(workflow.Exit, "exit", errors);
        foreach (var shared in workflow.SharedTransitions)
            ValidateTransition(shared, $"sharedTransitions[{shared.Key}]", errors);

        foreach (var state in workflow.States)
        {
            var statePath = $"states[{state.Key}]";
            ValidateBindings(state.OnEntries, $"{statePath}.onEntries", errors);
            ValidateBindings(state.OnExits, $"{statePath}.onExits", errors);
            foreach (var transition in state.Transitions)
                ValidateTransition(transition, $"{statePath}.transitions[{transition.Key}]", errors);
        }
    }

    private void ValidateFunction(Function function, IList<ValidationResult> errors)
    {
        if (function.Task is not null)
            ValidateBinding(function.Task, "task", errors);
        ValidateBindings(function.OnExecutionTasks, "onExecutionTasks", errors);
    }

    private void ValidateTransition(Transition? transition, string path, IList<ValidationResult> errors)
    {
        if (transition is null)
            return;
        ValidateBindings(transition.OnExecutionTasks, $"{path}.onExecutionTasks", errors);
    }

    private void ValidateBindings(IEnumerable<OnExecuteTask> bindings, string path, IList<ValidationResult> errors)
    {
        var index = 0;
        foreach (var binding in bindings)
            ValidateBinding(binding, $"{path}[{index++}]", errors);
    }

    /// <summary>Validates one binding at <paramref name="location"/> and appends its errors.</summary>
    public void ValidateBinding(OnExecuteTask binding, string location, IList<ValidationResult> errors)
    {
        if (binding.Mapping is not null && ConditionScriptLocations.IsDynamicExpresso(binding.Mapping.Location))
        {
            Add(errors, $"{location}.mapping",
                "A task binding mapping cannot use the dynamicExpresso location: it takes a script. " +
                "Author the declarative 'input' and 'output' blocks for expression-based mappings.");
        }

        if (!binding.HasDeclarativeMapping)
            return;

        if (binding.Mapping is not null)
        {
            Add(errors, location,
                "A binding cannot carry both a 'mapping' script and declarative 'input'/'output' blocks; use one.");
        }

        if (binding.Input is not null)
            ValidateBlock(binding.Input, $"{location}.input", isInput: true, errors);
        if (binding.Output is not null)
            ValidateBlock(binding.Output, $"{location}.output", isInput: false, errors);
    }

    private void ValidateBlock(MappingBlock block, string path, bool isInput, IList<ValidationResult> errors)
    {
        if (block.Select is not null)
        {
            var parsed = Parse(block.Select, $"{path}.select", errors);
            if (parsed is not null && isInput && !InputVariables.Contains(parsed.FirstSegment, StringComparer.Ordinal))
            {
                Add(errors, $"{path}.select",
                    $"An input 'select' path reads the variable scope and must start at one of: {string.Join(", ", InputVariables)}.");
            }
        }

        if (block.Into is not null)
            ValidateTarget(block.Into, $"{path}.into", isInput, errors);

        if (block.Set is null)
            return;

        var signature = ExpressionSignatures.Mapping(isInput ? ScopeKind.MappingInput : ScopeKind.MappingOutput);
        foreach (var (key, expression) in block.Set)
        {
            var keyPath = $"{path}.set['{key}']";
            ValidateTarget(key, keyPath, isInput, errors);

            if (string.IsNullOrWhiteSpace(expression))
            {
                Add(errors, keyPath, "The expression must not be empty.");
                continue;
            }

            if (expression.Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
            {
                Add(errors, keyPath,
                    $"The expression exceeds the maximum length ({ConditionScriptLocations.MaxDynamicExpressoExpressionLength}).");
                continue;
            }

            var error = isInput
                ? compiler.Compile<InputFn>(expression, signature) is { IsSuccess: false } f1 ? f1.Error.Message : null
                : compiler.Compile<OutputFn>(expression, signature) is { IsSuccess: false } f2 ? f2.Error.Message : null;
            if (error is not null)
                Add(errors, keyPath, $"The expression does not compile: {error}");
        }
    }

    private static void ValidateTarget(string path, string location, bool isInput, IList<ValidationResult> errors)
    {
        var parsed = Parse(path, location, errors);
        if (parsed is null)
            return;

        if (!parsed.IsWritable)
        {
            Add(errors, location, "A target path must name a property: array indexes and wildcards are not writable.");
            return;
        }

        if (isInput)
        {
            if (!InputSurfaces.Contains(parsed.FirstSegment, StringComparer.Ordinal))
            {
                Add(errors, location,
                    "An input target must start with $.body, $.headers, $.query or $.route.");
            }
        }
        else if (parsed.FirstSegment.Length == 0)
        {
            Add(errors, location, "An output target must be a property of the instance data, not the root '$'.");
        }
    }

    private static JsonPath? Parse(string text, string location, IList<ValidationResult> errors)
    {
        var parsed = JsonPath.Parse(text);
        if (parsed.IsSuccess)
            return parsed.Value;
        Add(errors, location, $"Invalid JSON path: {parsed.Error.Message}");
        return null;
    }

    private static void Add(IList<ValidationResult> errors, string location, string message) =>
        errors.Add(new ValidationResult($"{location}: {message}", [location]));
}
