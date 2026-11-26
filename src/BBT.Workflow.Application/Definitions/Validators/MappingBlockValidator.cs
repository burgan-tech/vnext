using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Workflow.Scripting.Declarative;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared.Json;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Sandbox;
using InputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Expressions.ExpressionConfig,
    BBT.Workflow.Scripting.Expressions.ExpressionHeaders, object>;
using OutputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Expressions.ExpressionConfig,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Expressions.ExpressionHeaders, object>;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Publish-time validation of the declarative <c>input</c>/<c>output</c> blocks of task bindings
/// (<see cref="OnExecuteTask"/>) wherever a component holds them: flow states (<c>onEntries</c>,
/// <c>onExits</c>), state / start / shared / well-known (<c>cancel</c>, <c>updateData</c>, <c>exit</c>)
/// transitions (<c>onExecutionTasks</c>), function (<c>task</c>, <c>onExecutionTasks</c>) and extension
/// (<c>task</c>) definitions; and the non-task declarative blocks: transition <c>output</c>, SubFlow
/// <c>input</c>/<c>output</c>, event <c>input</c> (workflow <c>event</c> and every transition's <c>event</c>), workflow
/// <c>output</c> and function <c>output</c>.
/// </summary>
/// <remarks>
/// Every path is parsed with <see cref="JsonPath"/> and every <c>set</c> expression is compiled with the
/// phase's <see cref="ExpressionSignatures.Mapping"/> signature through the shared singleton compiler, so
/// publish warms the compile cache; compile is validation only, nothing is stored. Errors read
/// <c>{jsonLocation}: {message}</c>, for example
/// <c>states[review].onEntries[0].output.set['$.kyc.score']: ...</c>. A binding with neither a mapping nor
/// blocks stays valid. An input target is a path into the task's <c>config</c> (<c>$.url</c>, <c>$.body.x</c>); which
/// fields a task TYPE reads is not checked here: a binding holds only a task reference, so the type is not resolvable at
/// publish.
/// </remarks>
public sealed class MappingBlockValidator(IExpressionCompiler compiler)
{
    /// <summary>Publish warning for a task <c>mapping</c> labelled with the <c>dynamicExpresso</c> location.</summary>
    public const string DynamicExpressoMappingLocationWarning =
        "The dynamicExpresso location is ignored on a task 'mapping': the mapping runs as a C# script. " +
        "Author the declarative 'input' and 'output' blocks for expression-based mappings.";

    /// <summary>
    /// Publish warning for a value slot (timer, timeout, timeout override, agent prompt, agent decision) labelled
    /// <c>dynamicExpresso</c> that does not compile as an expression. Never an error: such a script ran as C# before the
    /// label was honoured and still does (with a run-time warning).
    /// </summary>
    public const string ValueSlotNotAnExpressionWarning =
        "Labelled dynamicExpresso but does not compile as a Dynamic Expresso expression ({0}); it will run as a C# script " +
        "for compatibility. Rewrite it as an expression, or give the C# script a non-dynamicExpresso location.";

    private static readonly string[] InputVariables =
    [
        ExpressionScope.DataVariable, ExpressionScope.InputVariable, ExpressionScope.InstanceVariable,
        ExpressionScope.NowVariable, ExpressionScope.UserVariable, ExpressionScope.ContextVariable,
        ExpressionScope.TaskVariable
    ];

    /// <summary>
    /// Publish warning for a function output block that targets <c>$.statusCode</c> or <c>$.headers…</c> while
    /// <c>rawResponse</c> is <c>false</c>: the response is then <c>{ "functionKey": data }</c> and those members are ignored.
    /// </summary>
    public const string StatusCodeOrHeadersWithoutRawResponseWarning =
        "'rawResponse' is false: the function answers {{ \"<functionKey>\": data }} and ignores $.{0}. " +
        "Set 'rawResponse': true to send the status code and headers, or remove the target.";

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
                    ValidateTransitionInputMembers(attributes, result.ValidationErrors);
                    if (attributes.Deserialize<Workflow>(JsonSerializerConstants.JsonOptions) is { } workflow)
                        ValidateWorkflow(workflow, result);
                    break;
                case RuntimeSysSchemaInfo.Functions:
                    if (attributes.Deserialize<Function>(JsonSerializerConstants.JsonOptions) is { } function)
                        ValidateFunction(function, result);
                    break;
                case RuntimeSysSchemaInfo.Extensions:
                    if (attributes.Deserialize<Extension>(JsonSerializerConstants.JsonOptions) is { } extension
                        && extension.Task is not null)
                        ValidateBinding(extension.Task, "task", result.ValidationErrors, result.Warnings);
                    break;
                default:
                    break; // other component types carry no task bindings
            }
        }
        catch (JsonException)
        {
            // Reported by the component's own validator.
        }
        catch (ArgumentException)
        {
            // Reported by the component's own validator.
        }
    }

    private void ValidateWorkflow(Workflow workflow, ComponentValidationResult r)
    {
        ValidateEvent(workflow.Event, "event", r);
        if (workflow.OutputBlock is not null)
            ValidatePlan(workflow.OutputBlock, DeclarativeSiteKind.WorkflowOutput, "output", r.ValidationErrors);
        ValidateValueSlot(workflow.Timeout?.Mapping, "timeout.mapping", ValueSlotKind.Timer, r);
        ValidateTransition(workflow.StartTransition, "startTransition", r);
        ValidateTransition(workflow.Cancel, "cancel", r);
        ValidateTransition(workflow.UpdateData, "updateData", r);
        ValidateTransition(workflow.Exit, "exit", r);
        foreach (var shared in workflow.SharedTransitions)
            ValidateTransition(shared, $"sharedTransitions[{shared.Key}]", r);

        foreach (var state in workflow.States)
        {
            var statePath = $"states[{state.Key}]";
            ValidateBindings(state.OnEntries, $"{statePath}.onEntries", r);
            ValidateBindings(state.OnExits, $"{statePath}.onExits", r);
            ValidateSubFlowBlocks(state.SubFlow, $"{statePath}.subFlow", r);
            ValidateValueSlot(state.SubFlow?.Overrides?.Timeout?.Mapping, $"{statePath}.subFlow.overrides.timeout.mapping",
                ValueSlotKind.Timer, r);
            if (state.Agent?.Prompt is { IsScript: true } prompt)
                ValidateValueSlot(prompt.ToScriptCode(), $"{statePath}.agent.prompt", ValueSlotKind.AgentPrompt, r);
            ValidateValueSlot(state.Agent?.Decision?.Mapping, $"{statePath}.agent.decision.mapping", ValueSlotKind.AgentDecision, r);
            foreach (var transition in state.Transitions)
                ValidateTransition(transition, $"{statePath}.transitions[{transition.Key}]", r);
        }
    }

    private void ValidateFunction(Function function, ComponentValidationResult r)
    {
        if (function.Task is not null)
            ValidateBinding(function.Task, "task", r.ValidationErrors, r.Warnings);
        ValidateBindings(function.OnExecutionTasks, "onExecutionTasks", r);

        if (function.OutputBlock is not { } output)
            return;
        ValidatePlan(output, DeclarativeSiteKind.FunctionOutput, "output", r.ValidationErrors);
        if (!function.RawResponse)
            WarnResponseMembersIgnored(output, r.Warnings);
    }

    /// <summary>
    /// Without <c>rawResponse</c> a function answers <c>{ "functionKey": data }</c>: a <c>statusCode</c> or <c>headers</c>
    /// target is accepted (turning <c>rawResponse</c> on later makes it live) but is reported as ignored.
    /// </summary>
    private static void WarnResponseMembersIgnored(MappingBlock block, IList<ValidationResult> warnings)
    {
        if (block.Into is not null)
            WarnIfResponseMember(block.Into, "output.into", warnings);
        foreach (var key in block.Set?.Keys ?? Enumerable.Empty<string>())
            WarnIfResponseMember(key, $"output.set['{key}']", warnings);
    }

    private static void WarnIfResponseMember(string target, string location, IList<ValidationResult> warnings)
    {
        var parsed = JsonPath.Parse(target);
        if (parsed.IsSuccess && parsed.Value!.FirstSegment is "statusCode" or "headers")
        {
            Add(warnings, location, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                StatusCodeOrHeadersWithoutRawResponseWarning, parsed.Value.FirstSegment));
        }
    }

    /// <summary>
    /// An event's <c>input</c> block: exclusive with the <c>mapping</c> script and checked by the plan builder (targets
    /// <c>key</c> / <c>attributes</c>; no <c>data</c>, <c>instance</c> or <c>user</c>). An event with neither stays as
    /// today (the runtime answers <c>EventDefinitionMissing</c>).
    /// </summary>
    private void ValidateEvent(Events.Event? @event, string path, ComponentValidationResult r)
    {
        if (@event?.Input is null)
            return;
        if (@event.Mapping is not null)
            Add(r.ValidationErrors, path, "'mapping' and 'input' are mutually exclusive");
        ValidatePlan(@event.Input, DeclarativeSiteKind.EventInput, $"{path}.input", r.ValidationErrors);
    }

    private void ValidateTransition(Transition? transition, string path, ComponentValidationResult r)
    {
        if (transition is null)
            return;
        ValidateValueSlot(transition.Timer, $"{path}.timer", ValueSlotKind.Timer, r);
        ValidateBindings(transition.OnExecutionTasks, $"{path}.onExecutionTasks", r);
        ValidateEvent(transition.Event, $"{path}.event", r);

        if (transition.Output is null)
            return;
        if (transition.Mapping is not null)
            Add(r.ValidationErrors, path, "'mapping' and 'output' are mutually exclusive");
        ValidatePlan(transition.Output, DeclarativeSiteKind.TransitionOutput, $"{path}.output", r.ValidationErrors);
    }

    /// <summary>
    /// A SubFlow state's <c>input</c>/<c>output</c> blocks: exclusive with <c>mapping</c>, valid for an <c>S</c> SubFlow only,
    /// and checked by the same plan builder the runtime uses (target, select and expression rules).
    /// </summary>
    private void ValidateSubFlowBlocks(SubFlow? subFlow, string path, ComponentValidationResult r)
    {
        if (subFlow is null || (subFlow.Input is null && subFlow.Output is null))
            return;
        if (subFlow.Mapping is not null)
            Add(r.ValidationErrors, path, "'mapping' and the declarative 'input'/'output' blocks are mutually exclusive");
        if (subFlow.Type != SubFlowType.SubFlow)
        {
            Add(r.ValidationErrors, path,
                "declarative 'input'/'output' blocks apply to a SubFlow ('S') only; a SubProcess ('P') is started by its own task.");
            return;
        }

        if (subFlow.Input is not null)
            ValidatePlan(subFlow.Input, DeclarativeSiteKind.SubFlowInput, $"{path}.input", r.ValidationErrors);
        if (subFlow.Output is not null)
            ValidatePlan(subFlow.Output, DeclarativeSiteKind.SubFlowOutput, $"{path}.output", r.ValidationErrors);
    }

    // The plan builder is the single target / select / compile check of the non-task sites.
    // An empty block is rejected here (not on a task binding, whose empty block is shipped behaviour): an empty
    // transition output would silently drop the whole payload, an empty SubFlow block would do nothing at all.
    private void ValidatePlan(MappingBlock block, DeclarativeSiteKind kind, string path, IList<ValidationResult> errors)
    {
        if (block.Select is null && block.Into is null && (block.Set is null || block.Set.Count == 0))
        {
            Add(errors, path, "the block is empty: give it a 'select', an 'into' or a 'set' entry, or remove it.");
            return;
        }

        var plan = DeclarativeMappingPlan.Build(compiler, block, kind);
        if (!plan.IsSuccess)
            Add(errors, path, plan.Error.Message ?? "The declarative block is invalid.");
    }

    /// <summary>
    /// A transition takes only <c>output</c>. Deserialization drops an unknown <c>input</c> member silently, so it is
    /// found on the raw attributes of every transition holder.
    /// </summary>
    private static void ValidateTransitionInputMembers(JsonElement attributes, IList<ValidationResult> errors)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
            return;

        foreach (var name in new[] { "startTransition", "cancel", "updateData", "exit" })
        {
            if (attributes.TryGetProperty(name, out var single))
                CheckInput(single, name, errors);
        }

        if (attributes.TryGetProperty("sharedTransitions", out var shared) && shared.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in shared.EnumerateArray())
                CheckInput(t, $"sharedTransitions[{KeyOf(t)}]", errors);
        }

        if (!attributes.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array)
            return;
        foreach (var state in states.EnumerateArray())
        {
            if (state.ValueKind != JsonValueKind.Object
                || !state.TryGetProperty("transitions", out var transitions) || transitions.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var t in transitions.EnumerateArray())
                CheckInput(t, $"states[{KeyOf(state)}].transitions[{KeyOf(t)}]", errors);
        }
    }

    private static string? KeyOf(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
            ? key.GetString()
            : null;

    private static void CheckInput(JsonElement transition, string path, IList<ValidationResult> errors)
    {
        if (transition.ValueKind == JsonValueKind.Object && transition.TryGetProperty("input", out _))
            Add(errors, path, "'input' is not supported on a transition; use 'output'.");
    }

    /// <summary>
    /// A value slot labelled <c>dynamicExpresso</c> is compiled as an expression (publish warms the compile cache);
    /// one that does not compile is a warning, never an error (see <see cref="ValueSlotNotAnExpressionWarning"/>).
    /// </summary>
    private void ValidateValueSlot(ScriptCode? script, string path, ValueSlotKind kind, ComponentValidationResult r)
    {
        if (script is null || !ConditionScriptLocations.IsDynamicExpresso(script.Location) || script.IsReference)
            return;

        if (ValueSlotExpressionEvaluator.CompileError(compiler, script, kind) is { } error)
            Add(r.Warnings, path, string.Format(System.Globalization.CultureInfo.InvariantCulture, ValueSlotNotAnExpressionWarning, error));
    }

    private void ValidateBindings(IEnumerable<OnExecuteTask> bindings, string path, ComponentValidationResult r)
    {
        var index = 0;
        foreach (var binding in bindings)
            ValidateBinding(binding, $"{path}[{index++}]", r.ValidationErrors, r.Warnings);
    }

    /// <summary>
    /// Validates one binding at <paramref name="location"/> and appends its errors and, when
    /// <paramref name="warnings"/> is given, its non-blocking findings.
    /// </summary>
    public void ValidateBinding(
        OnExecuteTask binding, string location, IList<ValidationResult> errors, IList<ValidationResult>? warnings = null)
    {
        // The script engine never reads ScriptCode.Location, so a task mapping labelled dynamicExpresso has always run
        // as a C# IMapping script (and an empty one as a no-op). Keep that meaning; only tell the author.
        if (binding.Mapping is not null && warnings is not null
            && ConditionScriptLocations.IsDynamicExpresso(binding.Mapping.Location))
        {
            Add(warnings, $"{location}.mapping", DynamicExpressoMappingLocationWarning);
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
        // An input select has no default target and an input into no default source: one without the other never runs.
        if (isInput && block.Select is not null && block.Into is null)
            Add(errors, $"{path}.select", "An input 'select' needs an 'into' naming the task-config path it copies to.");
        if (isInput && block.Into is not null && block.Select is null)
            Add(errors, $"{path}.into", "An input 'into' needs a 'select' naming the value it copies.");

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
                ? CompileError<InputFn>(compiler, expression, signature)
                : CompileError<OutputFn>(compiler, expression, signature);
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

        if (parsed.FirstSegment.Length == 0)
        {
            Add(errors, location, isInput
                ? "An input target must be a field of the task's config ($.url, $.body.x), not the root '$'."
                : "An output target must be a property of the instance data, not the root '$'.");
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

    private static string? CompileError<TDelegate>(IExpressionCompiler compiler, string expression, ExpressionSignature signature)
        where TDelegate : Delegate
    {
        var compiled = compiler.Compile<TDelegate>(expression, signature);
        return compiled.IsSuccess ? null : compiled.Error.Message;
    }
}
