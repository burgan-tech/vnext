using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Scripting.Rules;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Provides consistent validation for workflow domain objects.
/// Validates workflow structure, states, transitions, labels, and error boundaries according to business rules.
/// </summary>
public class WorkflowValidator
{
    private readonly ErrorBoundaryValidator _errorBoundaryValidator = new();

    /// <summary>
    /// Validates the entire workflow definition.
    /// </summary>
    /// <param name="workflow">The workflow to validate.</param>
    /// <returns>A validation result containing any errors found.</returns>
    public WorkflowValidationResult Validate(Workflow workflow)
    {
        var result = new WorkflowValidationResult();

        var stateKeys = workflow.States.Select(s => s.Key).ToHashSet();
        foreach (var key in WellKnownStateKeys.ReservedTargetKeys)
        {
            stateKeys.AddIfNotContains(key);
        }

        // Workflow level validations
        ValidateWorkflowLabels(workflow, result);
        ValidateTimeoutInStates(workflow, result, stateKeys);
        ValidateStartTransition(workflow, result);
        ValidateUpdateDataTransition(workflow, result);
        ValidateTransitionInStates(workflow, result, stateKeys);
        ValidateStateCountAndTypes(workflow, result);
        ValidateRoleGrants(workflow.QueryRoles, $"{nameof(Workflow)}.{nameof(Workflow.QueryRoles)}", result);

        // State level validations
        ValidateStateLabels(workflow, result);
        ValidateStateAliases(workflow, result);
        ValidateStateNotifications(workflow, result);
        ValidateWizardStateTransitions(workflow, result);
        ValidateDefaultAutoTransitions(workflow, result);
        foreach (var state in workflow.States)
            ValidateRoleGrants(state.QueryRoles, $"{nameof(Workflow)}.States[{state.Key}].{nameof(State.QueryRoles)}", result);

        // Transition level validations
        ValidateTransitionLabels(workflow, result);
        ValidateTransitionRules(workflow, result);

        // Script slot validations (transition-level slots run inside ValidateSingleTransition)
        ValidateWorkflowScriptCodes(workflow, result);

        // Error boundary validations
        ValidateErrorBoundaries(workflow, result, stateKeys);

        return result;
    }

    #region Workflow Level Validations

    /// <summary>
    /// Validates that workflow has at least one label defined.
    /// </summary>
    private void ValidateWorkflowLabels(Workflow workflow, WorkflowValidationResult result)
    {
        if (workflow.Labels.Count == 0)
        {
            result.AddError(new ValidationResult(
                "Workflow must have at least one label defined.",
                [$"{nameof(Workflow)}.{nameof(Workflow.Labels)}"]));
        }
    }

    /// <summary>
    /// Validates timeout target references valid states.
    /// </summary>
    private void ValidateTimeoutInStates(Workflow workflow, WorkflowValidationResult result, HashSet<string> stateKeys)
    {
        if (workflow.Timeout != null)
        {
            if (!stateKeys.Contains(workflow.Timeout.Target))
            {
                result.AddError(new ValidationResult(
                    $"The 'target' value in the timeout does not match any state '{workflow.Timeout.Target}'.",
                    [$"{nameof(Workflow)}.{nameof(WorkflowTimeout)}.{nameof(WorkflowTimeout.Target)}"]));
            }

            if (workflow.Timeout.Mapping != null && workflow.Timeout.Timer == null)
            {
                result.AddError(new ValidationResult(
                    "When timeout mapping is defined, static timer configuration is also required as fallback.",
                    [$"{nameof(Workflow)}.{nameof(WorkflowTimeout)}.{nameof(WorkflowTimeout.Timer)}"]));
            }
        }
    }

    /// <summary>
    /// Validates that workflow has a start transition.
    /// </summary>
    private void ValidateStartTransition(Workflow workflow, WorkflowValidationResult result)
    {
        if (workflow.StartTransition == null)
        {
            result.AddError(new ValidationResult(
                "Workflow must have a start transition.",
                [$"{nameof(Workflow)}.{nameof(Workflow.StartTransition)}"]));
        }
    }

    /// <summary>
    /// Validates that UpdateData transition must have target value of "$self".
    /// </summary>
    private void ValidateUpdateDataTransition(Workflow workflow, WorkflowValidationResult result)
    {
        if (workflow.UpdateData != null && workflow.UpdateData.Target != WellKnownStateKeys.Self)
        {
            result.AddError(new ValidationResult(
                $"UpdateData transition must have target value of '$self'. Found: '{workflow.UpdateData.Target}'.",
                [$"{nameof(Workflow)}.{nameof(Workflow.UpdateData)}.{nameof(Transition.Target)}"]));
        }
    }

    /// <summary>
    /// Validates transition targets and availableIn references valid states.
    /// </summary>
    private void ValidateTransitionInStates(Workflow workflow, WorkflowValidationResult result, HashSet<string> stateKeys)
    {
        // Validate StartTransition availableIn
        ValidateAvailableIn(
            workflow.StartTransition,
            "StartTransition",
            $"{nameof(Workflow)}.{nameof(Workflow.StartTransition)}",
            result,
            stateKeys);

        // Validate the well-known workflow-level transitions' availableIn. These carry role grants
        // that the discovery and authorize surfaces now enforce, so their state keys and grants must
        // be validated exactly like a shared transition's.
        if (workflow.Cancel != null)
            ValidateAvailableIn(workflow.Cancel, "cancel transition", $"{nameof(Workflow)}.{nameof(Workflow.Cancel)}", result, stateKeys);

        if (workflow.UpdateData != null)
            ValidateAvailableIn(workflow.UpdateData, "updateData transition", $"{nameof(Workflow)}.{nameof(Workflow.UpdateData)}", result, stateKeys);

        if (workflow.Exit != null)
            ValidateAvailableIn(workflow.Exit, "exit transition", $"{nameof(Workflow)}.{nameof(Workflow.Exit)}", result, stateKeys);

        // Validate StartTransition target
        if (!workflow.StartTransition.Target.IsNullOrEmpty())
        {
            if (!stateKeys.Contains(workflow.StartTransition.Target))
            {
                result.AddError(new ValidationResult(
                    $"The 'target' value in StartTransition does not match any state '{workflow.StartTransition.Target}'.",
                    [$"{nameof(Workflow)}.{nameof(Workflow.StartTransition)}.{nameof(Transition.Target)}"]));
            }
        }

        // Validate SharedTransitions availableIn
        foreach (var transition in workflow.SharedTransitions)
        {
            ValidateAvailableIn(
                transition,
                $"shared transition '{transition.Key}'",
                $"{nameof(Workflow)}.{nameof(Workflow.SharedTransitions)}[{transition.Key}]",
                result,
                stateKeys);

            // Validate SharedTransitions target
            if (!string.IsNullOrEmpty(transition.Target) && !stateKeys.Contains(transition.Target))
            {
                result.AddError(new ValidationResult(
                    $"The 'target' value in shared transition '{transition.Key}' does not match any state '{transition.Target}'.",
                    [$"{nameof(Workflow)}.{nameof(Workflow.SharedTransitions)}[{transition.Key}].{nameof(Transition.Target)}"]));
            }
        }

        // Validate State transitions target
        foreach (var state in workflow.States)
        {
            foreach (var transition in state.Transitions.Where(t => !string.IsNullOrEmpty(t.Target)))
            {
                if (!stateKeys.Contains(transition.Target))
                {
                    result.AddError(new ValidationResult(
                        $"The 'target' value in state '{state.Key}' transition '{transition.Key}' does not match any state '{transition.Target}'.",
                        [$"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Transitions)}[{transition.Key}].{nameof(Transition.Target)}"]));
                }
            }
        }
    }

    /// <summary>
    /// Validates state count and that exactly one initial state exists.
    /// </summary>
    private void ValidateStateCountAndTypes(Workflow workflow, WorkflowValidationResult result)
    {
        if (workflow.States.Count < 1)
        {
            result.AddError(new ValidationResult(
                "Workflow must contain at least one state.",
                [$"{nameof(Workflow)}.{nameof(Workflow.States)}"]));
            return;
        }

        var initialStateCount = workflow.States.Count(s => s.StateType == StateType.Initial);
        if (initialStateCount != 1)
        {
            result.AddError(new ValidationResult(
                $"Workflow must contain exactly one initial state. Found: {initialStateCount}.",
                [$"{nameof(Workflow)}.{nameof(Workflow.States)}"]));
        }
    }

    #endregion

    #region State Level Validations

    /// <summary>
    /// Validates that each state has at least one label defined.
    /// </summary>
    private void ValidateStateLabels(Workflow workflow, WorkflowValidationResult result)
    {
        foreach (var state in workflow.States)
        {
            if (state.Labels.Count == 0)
            {
                result.AddError(new ValidationResult(
                    $"State '{state.Key}' must have at least one label defined.",
                    [$"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Labels)}"]));
            }
        }
    }

    /// <summary>
    /// Validates state aliases. The alias array is optional, but when present each entry must
    /// declare a name, at least one label, and at least one role grant. Dynamic role references
    /// are additionally validated for correct path format.
    /// </summary>
    private void ValidateStateAliases(Workflow workflow, WorkflowValidationResult result)
    {
        foreach (var state in workflow.States)
        {
            if (state.Aliases.Count == 0)
                continue;

            var index = 0;
            foreach (var alias in state.Aliases)
            {
                var aliasPath = $"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Aliases)}[{index}]";

                if (string.IsNullOrWhiteSpace(alias.Name))
                {
                    result.AddError(new ValidationResult(
                        $"Alias at index {index} in state '{state.Key}' must have a name defined.",
                        [$"{aliasPath}.{nameof(StateAlias.Name)}"]));
                }

                if (alias.Labels.Count == 0)
                {
                    result.AddError(new ValidationResult(
                        $"Alias '{alias.Name}' in state '{state.Key}' must have at least one label defined.",
                        [$"{aliasPath}.{nameof(StateAlias.Labels)}"]));
                }

                if (alias.Roles.Count == 0)
                {
                    result.AddError(new ValidationResult(
                        $"Alias '{alias.Name}' in state '{state.Key}' must have at least one role defined.",
                        [$"{aliasPath}.{nameof(StateAlias.Roles)}"]));
                }
                else
                {
                    ValidateRoleGrants(alias.Roles, $"{aliasPath}.{nameof(StateAlias.Roles)}", result);
                }

                index++;
            }
        }
    }

    /// <summary>
    /// Validates state notification entries. The notifications array is optional, but when present each entry must:
    /// - define a mapping (mapping is required);
    /// - use a supported type — only <see cref="StateNotificationType.State"/> is processed today.
    /// NOTE (temporary): <see cref="StateNotificationType.Command"/> is reserved for future use and is
    /// rejected for now. When command notifications go live, relax the type check below to allow it.
    /// </summary>
    private void ValidateStateNotifications(Workflow workflow, WorkflowValidationResult result)
    {
        foreach (var state in workflow.States)
        {
            if (state.Notifications.Count == 0)
                continue;

            var index = 0;
            foreach (var notification in state.Notifications)
            {
                var notificationPath = $"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Notifications)}[{index}]";

                // Mapping is required.
                if (!notification.HasMapping)
                {
                    result.AddError(new ValidationResult(
                        $"Notification at index {index} in state '{state.Key}' must define a mapping.",
                        [$"{notificationPath}.{nameof(StateNotification.Mapping)}"]));
                }

                // Only 'state' is supported for now.
                // NOTE (temporary): 'command' is reserved for future use. When command notifications
                // are implemented, relax this check to also allow StateNotificationType.Command.
                if (notification.Type != StateNotificationType.State)
                {
                    result.AddError(new ValidationResult(
                        $"Notification at index {index} in state '{state.Key}' has unsupported type '{notification.Type}'. Only '{StateNotificationType.State}' is supported.",
                        [$"{notificationPath}.{nameof(StateNotification.Type)}"]));
                }

                index++;
            }
        }
    }

    /// <summary>
    /// Validates that wizard states have at most one transition.
    /// </summary>
    private void ValidateWizardStateTransitions(Workflow workflow, WorkflowValidationResult result)
    {
        foreach (var state in workflow.States.Where(s => s.StateType == StateType.Wizard))
        {
            if (state.Transitions.Count > 1)
            {
                result.AddError(new ValidationResult(
                    $"Wizard state '{state.Key}' can have at most one transition. Found: {state.Transitions.Count}.",
                    [$"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Transitions)}"]));
            }
        }
    }

    /// <summary>
    /// Validates DefaultAutoTransition rules:
    /// - Each state can have at most one DefaultAutoTransition
    /// - DefaultAutoTransition must have TriggerType.Automatic
    /// </summary>
    private void ValidateDefaultAutoTransitions(Workflow workflow, WorkflowValidationResult result)
    {
        foreach (var state in workflow.States)
        {
            var defaultAutoTransitions = state.Transitions
                .Where(t => t.TriggerKind == TransitionKind.DefaultAutoTransition)
                .ToList();

            // Validate at most one DefaultAutoTransition per state
            if (defaultAutoTransitions.Count > 1)
            {
                result.AddError(new ValidationResult(
                    $"State '{state.Key}' can have at most one DefaultAutoTransition. Found: {defaultAutoTransitions.Count}.",
                    [$"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Transitions)}"]));
            }

            // Validate DefaultAutoTransition must be Automatic trigger type
            foreach (var transition in defaultAutoTransitions)
            {
                if (transition.TriggerType != TriggerType.Automatic)
                {
                    result.AddError(new ValidationResult(
                        $"DefaultAutoTransition '{transition.Key}' in state '{state.Key}' must have TriggerType.Automatic.",
                        [$"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Transitions)}[{transition.Key}].{nameof(Transition.TriggerType)}"]));
                }
            }
        }
    }

    #endregion

    #region Transition Level Validations

    /// <summary>
    /// Validates that each transition has at least one label defined.
    /// </summary>
    private void ValidateTransitionLabels(Workflow workflow, WorkflowValidationResult result)
    {
        // Validate StartTransition labels
        if (workflow.StartTransition != null && workflow.StartTransition.Labels.Count == 0)
        {
            result.AddError(new ValidationResult(
                "StartTransition must have at least one label defined.",
                [$"{nameof(Workflow)}.{nameof(Workflow.StartTransition)}.{nameof(Transition.Labels)}"]));
        }

        // Validate SharedTransitions labels
        foreach (var transition in workflow.SharedTransitions)
        {
            if (transition.Labels.Count == 0)
            {
                result.AddError(new ValidationResult(
                    $"Shared transition '{transition.Key}' must have at least one label defined.",
                    [$"{nameof(Workflow)}.{nameof(Workflow.SharedTransitions)}[{transition.Key}].{nameof(Transition.Labels)}"]));
            }
        }

        // Validate State transitions labels
        foreach (var state in workflow.States)
        {
            foreach (var transition in state.Transitions)
            {
                if (transition.Labels.Count == 0)
                {
                    result.AddError(new ValidationResult(
                        $"Transition '{transition.Key}' in state '{state.Key}' must have at least one label defined.",
                        [$"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Transitions)}[{transition.Key}].{nameof(Transition.Labels)}"]));
                }
            }
        }

        // Validate Cancel transition labels (if exists)
        if (workflow.Cancel != null && workflow.Cancel.Labels.Count == 0)
        {
            result.AddError(new ValidationResult(
                "Cancel transition must have at least one label defined.",
                [$"{nameof(Workflow)}.{nameof(Workflow.Cancel)}.{nameof(Transition.Labels)}"]));
        }
    }

    /// <summary>
    /// Validates transition properties based on trigger type.
    /// - Auto (Automatic): rule required, timer/schema/view/mapping not allowed
    /// - Schedule (Scheduled): timer required, rule/schema/view/mapping not allowed
    /// - AvailableIn: only allowed in SharedTransitions
    /// - Other (Manual, Event): rule/timer not allowed, others optional
    /// </summary>
    private void ValidateTransitionRules(Workflow workflow, WorkflowValidationResult result)
    {
        // Validate StartTransition - should not have AvailableIn (it's not a shared transition)
        if (workflow.StartTransition != null)
        {
            ValidateSingleTransition(workflow.StartTransition, "StartTransition", result, isSharedTransition: false);
        }

        // Validate well-known workflow-level transitions — AvailableIn is allowed (same as shared transitions).
        // These carry role grants that the runtime now enforces when building availableTransitions,
        // so they must go through the same role-grant and trigger-type validation as every other transition.
        if (workflow.Cancel != null)
        {
            ValidateSingleTransition(workflow.Cancel, "Cancel", result, isSharedTransition: true);
        }

        if (workflow.UpdateData != null)
        {
            ValidateSingleTransition(workflow.UpdateData, "UpdateData", result, isSharedTransition: true);
        }

        if (workflow.Exit != null)
        {
            ValidateSingleTransition(workflow.Exit, "Exit", result, isSharedTransition: true);
        }

        // Validate SharedTransitions - AvailableIn is allowed here
        foreach (var transition in workflow.SharedTransitions)
        {
            ValidateSingleTransition(transition, $"SharedTransitions[{transition.Key}]", result, isSharedTransition: true);
        }

        // Validate State transitions - AvailableIn not allowed
        foreach (var state in workflow.States)
        {
            foreach (var transition in state.Transitions)
            {
                ValidateSingleTransition(transition, $"States[{state.Key}].Transitions[{transition.Key}]", result, isSharedTransition: false);
            }
        }
    }

    /// <summary>
    /// Validates a single transition based on its trigger type and context.
    /// </summary>
    private void ValidateSingleTransition(
        Transition transition,
        string transitionPath,
        WorkflowValidationResult result,
        bool isSharedTransition)
    {
        var basePath = $"{nameof(Workflow)}.{transitionPath}";

        // AvailableIn is only valid in SharedTransitions
        if (!isSharedTransition && transition.AvailableIn.Count > 0)
        {
            result.AddError(new ValidationResult(
                $"'AvailableIn' is only allowed in shared transitions. Found in '{transitionPath}'.",
                [$"{basePath}.{nameof(Transition.AvailableIn)}"]));
        }

        ValidateRoleGrants(transition.Roles, $"{basePath}.{nameof(Transition.Roles)}", result);
        ValidateTransitionScriptCodes(transition, basePath, result);

        switch (transition.TriggerType)
        {
            case TriggerType.Automatic:
                ValidateAutoTransition(transition, basePath, result);
                break;

            case TriggerType.Scheduled:
                ValidateScheduledTransition(transition, basePath, result);
                break;

            case TriggerType.Manual:
            case TriggerType.Event:
                ValidateManualOrEventTransition(transition, basePath, result);
                break;
        }
    }

    /// <summary>
    /// Validates Auto (Automatic) transition rules:
    /// - Rule is required (except for DefaultAutoTransition which acts as fallback)
    /// - Timer, Schema, View, and Mapping are not allowed
    /// </summary>
    private void ValidateAutoTransition(Transition transition, string basePath, WorkflowValidationResult result)
    {
        // Rule is required for Auto transitions, except for DefaultAutoTransition (fallback)
        if (transition.Rule == null && transition.TriggerKind != TransitionKind.DefaultAutoTransition)
        {
            result.AddError(new ValidationResult(
                $"Auto transition '{transition.Key}' must have a rule defined.",
                [$"{basePath}.{nameof(Transition.Rule)}"]));
        }

        // Timer is not allowed
        if (transition.Timer != null)
        {
            result.AddError(new ValidationResult(
                $"Auto transition '{transition.Key}' cannot have a timer definition.",
                [$"{basePath}.{nameof(Transition.Timer)}"]));
        }

        // Schema is not allowed
        if (transition.HasSchema)
        {
            result.AddError(new ValidationResult(
                $"Auto transition '{transition.Key}' cannot have a schema definition.",
                [$"{basePath}.{nameof(Transition.Schema)}"]));
        }

        // View is not allowed
        if (transition.View != null)
        {
            result.AddError(new ValidationResult(
                $"Auto transition '{transition.Key}' cannot have a view definition.",
                [$"{basePath}.{nameof(Transition.View)}"]));
        }

        // Mapping is not allowed
        if (transition.Mapping != null)
        {
            result.AddError(new ValidationResult(
                $"Auto transition '{transition.Key}' cannot have a mapping definition.",
                [$"{basePath}.{nameof(Transition.Mapping)}"]));
        }

        ValidateDynamicExpressoRule(transition, basePath, result);
    }

    /// <summary>
    /// When rule location selects Dynamic Expresso, the decoded expression must be non-empty and within length limits.
    /// <para>
    /// This owns the emptiness/decodability checks for a Dynamic Expresso rule rather than deferring to
    /// <see cref="ScriptCodeValidator"/>: the generic advice ("inline the script body into 'code'") is
    /// wrong for an inline boolean expression. <see cref="ValidateTransitionScriptCodes"/> therefore skips
    /// the rule slot for this location so the author gets one accurate error, not two.
    /// </para>
    /// </summary>
    private static void ValidateDynamicExpressoRule(Transition transition, string basePath, WorkflowValidationResult result)
    {
        if (transition.Rule == null || !ConditionScriptLocations.IsDynamicExpresso(transition.Rule.Location))
            return;

        string code;
        try
        {
            code = transition.Rule.DecodedCode.Trim();
        }
        catch (InvalidOperationException)
        {
            result.AddError(new ValidationResult(
                $"Auto transition '{transition.Key}' Dynamic Expresso rule has invalid Base64 or encoding.",
                [$"{basePath}.{nameof(Transition.Rule)}.{nameof(ScriptCode.Code)}"]));
            return;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            result.AddError(new ValidationResult(
                $"Auto transition '{transition.Key}' Dynamic Expresso rule must contain a non-empty expression.",
                [$"{basePath}.{nameof(Transition.Rule)}.{nameof(ScriptCode.Code)}"]));
            return;
        }

        if (code.Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
        {
            result.AddError(new ValidationResult(
                $"Auto transition '{transition.Key}' Dynamic Expresso rule exceeds maximum length ({ConditionScriptLocations.MaxDynamicExpressoExpressionLength}).",
                [$"{basePath}.{nameof(Transition.Rule)}.{nameof(ScriptCode.Code)}"]));
        }
    }

    /// <summary>
    /// Validates Scheduled transition rules:
    /// - Timer is required
    /// - Rule, Schema, View, and Mapping are not allowed
    /// </summary>
    private void ValidateScheduledTransition(Transition transition, string basePath, WorkflowValidationResult result)
    {
        // Timer is required for Scheduled transitions
        if (transition.Timer == null)
        {
            result.AddError(new ValidationResult(
                $"Scheduled transition '{transition.Key}' must have a timer defined.",
                [$"{basePath}.{nameof(Transition.Timer)}"]));
        }

        // Rule is not allowed
        if (transition.Rule != null)
        {
            result.AddError(new ValidationResult(
                $"Scheduled transition '{transition.Key}' cannot have a rule definition.",
                [$"{basePath}.{nameof(Transition.Rule)}"]));
        }

        // Schema is not allowed
        if (transition.HasSchema)
        {
            result.AddError(new ValidationResult(
                $"Scheduled transition '{transition.Key}' cannot have a schema definition.",
                [$"{basePath}.{nameof(Transition.Schema)}"]));
        }

        // View is not allowed
        if (transition.View != null)
        {
            result.AddError(new ValidationResult(
                $"Scheduled transition '{transition.Key}' cannot have a view definition.",
                [$"{basePath}.{nameof(Transition.View)}"]));
        }

        // Mapping is not allowed
        if (transition.Mapping != null)
        {
            result.AddError(new ValidationResult(
                $"Scheduled transition '{transition.Key}' cannot have a mapping definition.",
                [$"{basePath}.{nameof(Transition.Mapping)}"]));
        }
    }

    /// <summary>
    /// Validates Manual or Event transition rules:
    /// - Rule and Timer are not allowed
    /// - Schema, View, and Mapping are optional
    /// </summary>
    private void ValidateManualOrEventTransition(Transition transition, string basePath, WorkflowValidationResult result)
    {
        // Rule is not allowed
        if (transition.Rule != null)
        {
            result.AddError(new ValidationResult(
                $"Manual/Event transition '{transition.Key}' cannot have a rule definition.",
                [$"{basePath}.{nameof(Transition.Rule)}"]));
        }

        // Timer is not allowed
        if (transition.Timer != null)
        {
            result.AddError(new ValidationResult(
                $"Manual/Event transition '{transition.Key}' cannot have a timer definition.",
                [$"{basePath}.{nameof(Transition.Timer)}"]));
        }

        // Schema, View, Mapping are optional - no validation needed
    }

    /// <summary>
    /// Validates a transition's <c>availableIn</c> list: every entry must name an existing state, no
    /// state may be listed twice, and any per-state role grants must be well formed.
    /// <para>
    /// Duplicates matter because <see cref="Transition.FindAvailableIn"/> takes the first match — a
    /// second entry for the same state is silently dead, and if it is the one carrying the role
    /// narrowing the restriction never applies.
    /// </para>
    /// </summary>
    /// <param name="transition">Transition whose availableIn is validated.</param>
    /// <param name="contextLabel">Human-readable transition description used in error messages.</param>
    /// <param name="basePath">Dotted member path of the transition, without the trailing member name.</param>
    /// <param name="result">Accumulating validation result.</param>
    /// <param name="stateKeys">All state keys declared by the workflow, plus reserved target keys.</param>
    private static void ValidateAvailableIn(
        Transition transition,
        string contextLabel,
        string basePath,
        WorkflowValidationResult result,
        HashSet<string> stateKeys)
    {
        if (transition.AvailableIn.Count == 0)
            return;

        var memberPath = $"{basePath}.{nameof(Transition.AvailableIn)}";
        var seenStates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in transition.AvailableIn)
        {
            if (!stateKeys.Contains(entry.State))
            {
                result.AddError(new ValidationResult(
                    $"The 'availableIn' value in {contextLabel} does not match any state '{entry.State}'.",
                    [memberPath]));
            }

            if (!seenStates.Add(entry.State))
            {
                result.AddError(new ValidationResult(
                    $"The 'availableIn' list in {contextLabel} lists state '{entry.State}' more than once. Only the first entry takes effect.",
                    [memberPath]));
            }

            ValidateRoleGrants(entry.Roles, $"{memberPath}[{entry.State}].{nameof(AvailableInEntry.Roles)}", result);
        }
    }

    /// <summary>
    /// Validates a collection of role grants, checking dynamic role references for correct format.
    /// <para>
    /// Static role names (<c>backoffice.operator</c>) and the four predefined instance roles
    /// (<c>$InstanceStarter</c>, <c>$PreviousUser</c>, <c>$InstanceBehalfOfStarter</c>,
    /// <c>$PreviousBehalfOfUser</c>) are free-form and carry nothing to validate — only grants that
    /// declare dynamic-role intent via a qualifier prefix are checked, and only against the exact
    /// rules the runtime parser applies. Classification lives in
    /// <see cref="DynamicRoleGrant.Classify"/> so this rule cannot drift from
    /// <see cref="DynamicRoleGrant.TryParse"/>.
    /// </para>
    /// </summary>
    private static void ValidateRoleGrants(
        IReadOnlyCollection<RoleGrant> roleGrants,
        string context,
        WorkflowValidationResult result)
    {
        if (roleGrants.Count == 0)
            return;

        const string contextPrefix = "$.context.";

        foreach (var grant in roleGrants)
        {
            var message = DynamicRoleGrant.Classify(grant.Role) switch
            {
                DynamicRoleFormat.MissingContextPrefix =>
                    $"Dynamic role '{grant.Role}' in '{context}' has an invalid path. Path must start with '{contextPrefix}' (case-sensitive).",
                DynamicRoleFormat.EmptyNavigationPath =>
                    $"Dynamic role '{grant.Role}' in '{context}' has an empty navigation path after '{contextPrefix}'.",
                _ => null
            };

            if (message != null)
                result.AddError(new ValidationResult(message, [context]));
        }
    }

    #endregion

    #region Script Slot Validations

    /// <summary>
    /// Validates every workflow- and state-level script slot. Transition slots are covered by
    /// <see cref="ValidateTransitionScriptCodes"/>, which runs for each transition family
    /// (start / cancel / updateData / exit / shared / state) from <see cref="ValidateSingleTransition"/>.
    /// <para>
    /// A slot published with only a <c>location</c> is never executed and nothing downstream complains —
    /// see <see cref="ScriptCodeValidator"/>. Every slot the runtime can compile must be listed here, so
    /// add the new path whenever a definition object gains a <see cref="ScriptCode"/> property.
    /// </para>
    /// </summary>
    private static void ValidateWorkflowScriptCodes(Workflow workflow, WorkflowValidationResult result)
    {
        var errors = result.ValidationErrors;

        ScriptCodeValidator.Validate(
            workflow.Output, $"{nameof(Workflow)}.{nameof(Workflow.Output)}", errors);

        ScriptCodeValidator.Validate(
            workflow.Timeout?.Mapping,
            $"{nameof(Workflow)}.{nameof(Workflow.Timeout)}.{nameof(WorkflowTimeout.Mapping)}",
            errors);

        foreach (var state in workflow.States)
        {
            var statePath = $"{nameof(Workflow)}.States[{state.Key}]";

            ValidateTaskScriptCodes(state.OnEntries, $"{statePath}.{nameof(State.OnEntries)}", errors);
            ValidateTaskScriptCodes(state.OnExits, $"{statePath}.{nameof(State.OnExits)}", errors);

            ScriptCodeValidator.Validate(
                state.SubFlow?.Mapping,
                $"{statePath}.{nameof(State.SubFlow)}.{nameof(SubFlow.Mapping)}",
                errors);

            foreach (var (notification, index) in state.Notifications.Select((n, i) => (n, i)))
            {
                var path = $"{statePath}.{nameof(State.Notifications)}[{index}]";
                ScriptCodeValidator.Validate(notification.Rule, $"{path}.{nameof(StateNotification.Rule)}", errors);
                ScriptCodeValidator.Validate(notification.Mapping, $"{path}.{nameof(StateNotification.Mapping)}", errors);
            }

            ValidateViewRules(state.View, $"{statePath}.{nameof(State.View)}", errors);
        }
    }

    /// <summary>
    /// Validates the script slots a single transition can carry, including its OnExecute task mappings
    /// and view-selection rules.
    /// </summary>
    private static void ValidateTransitionScriptCodes(
        Transition transition,
        string basePath,
        WorkflowValidationResult result)
    {
        var errors = result.ValidationErrors;

        ScriptCodeValidator.Validate(transition.Timer, $"{basePath}.{nameof(Transition.Timer)}", errors);
        ScriptCodeValidator.Validate(transition.Mapping, $"{basePath}.{nameof(Transition.Mapping)}", errors);

        // ValidateDynamicExpressoRule already covers the Dynamic Expresso rule form with an accurate
        // message; only the script-body form is this pass's business.
        if (!ConditionScriptLocations.IsDynamicExpresso(transition.Rule?.Location))
        {
            ScriptCodeValidator.Validate(transition.Rule, $"{basePath}.{nameof(Transition.Rule)}", errors);
        }

        ValidateTaskScriptCodes(
            transition.OnExecutionTasks, $"{basePath}.{nameof(Transition.OnExecutionTasks)}", errors);

        ValidateViewRules(transition.View, $"{basePath}.{nameof(Transition.View)}", errors);
        ValidateSchemaRules(transition, $"{basePath}.{nameof(Transition.Schema)}", errors);
    }

    /// <summary>
    /// Validates the selection rule of each entry in a transition's schema selection, and that the
    /// entries form a reachable chain. Like view selection, a broken rule compiles mid-request; unlike
    /// view selection, a schema also gates request-body validation, so an entry that can never fire
    /// silently changes what the runtime accepts.
    /// </summary>
    private static void ValidateSchemaRules(
        Transition transition,
        string basePath,
        IList<ValidationResult> errors)
    {
        var selection = transition.Schema;
        if (selection is null)
            return;

        var entries = selection.Schemas;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            ScriptCodeValidator.Validate(entry.Rule, $"{basePath}[{index}].{nameof(SchemaEntry.Rule)}", errors);

            // A rule-less entry always matches, so it wins from here on and everything after it is dead.
            if (entry.Rule == null && index != entries.Count - 1)
            {
                errors.Add(new ValidationResult(
                    $"Transition '{transition.Key}' declares a schema entry without a rule at index {index}, " +
                    $"but it is not the last entry. A rule-less entry always matches, so the {entries.Count - index - 1} " +
                    "entry/entries after it can never be selected. Move it to the end.",
                    [$"{basePath}[{index}]"]));
            }
        }
    }

    /// <summary>
    /// Validates the mapping of each task in an OnExecute collection.
    /// </summary>
    private static void ValidateTaskScriptCodes(
        IEnumerable<OnExecuteTask> tasks,
        string basePath,
        IList<ValidationResult> errors)
    {
        foreach (var (task, index) in tasks.Select((t, i) => (t, i)))
        {
            ScriptCodeValidator.Validate(
                task.Mapping, $"{basePath}[{index}].{nameof(OnExecuteTask.Mapping)}", errors);
        }
    }

    /// <summary>
    /// Validates the selection rule of each entry in a view definition. A broken rule here does not
    /// degrade gracefully: view selection compiles the rule mid-request.
    /// </summary>
    private static void ValidateViewRules(
        ViewDefinition? viewDefinition,
        string basePath,
        IList<ValidationResult> errors)
    {
        if (viewDefinition is null)
            return;

        foreach (var (entry, index) in viewDefinition.Views.Select((v, i) => (v, i)))
        {
            ScriptCodeValidator.Validate(entry.Rule, $"{basePath}[{index}].{nameof(ViewEntry.Rule)}", errors);
        }
    }

    #endregion

    #region Error Boundary Validations

    /// <summary>
    /// Validates all error boundaries in the workflow (Workflow, State, Transition, SubFlow levels).
    /// </summary>
    private void ValidateErrorBoundaries(Workflow workflow, WorkflowValidationResult result, HashSet<string> stateKeys)
    {
        // Collect all valid transition keys in the workflow
        var transitionKeys = CollectTransitionKeys(workflow);

        // Validate Workflow-level (Global) ErrorBoundary
        if (workflow.ErrorBoundary != null)
        {
            var errors = _errorBoundaryValidator.Validate(
                workflow.ErrorBoundary,
                $"{nameof(Workflow)}.{nameof(Workflow.ErrorBoundary)}",
                stateKeys,
                transitionKeys);
            foreach (var error in errors)
            {
                result.AddError(error);
            }
        }

        // Validate State-level ErrorBoundaries
        foreach (var state in workflow.States)
        {
            ValidateStateErrorBoundary(state, result, stateKeys, transitionKeys);
        }

        // Validate Transition-level OnExecuteTask ErrorBoundaries
        ValidateTransitionTaskErrorBoundaries(workflow, result, stateKeys, transitionKeys);
    }

    /// <summary>
    /// Collects all transition keys defined in the workflow.
    /// Includes StartTransition, Cancel, SharedTransitions, and State transitions.
    /// </summary>
    private static HashSet<string> CollectTransitionKeys(Workflow workflow)
    {
        var transitionKeys = new HashSet<string>(StringComparer.Ordinal);

        // StartTransition
        if (workflow.StartTransition != null && !string.IsNullOrEmpty(workflow.StartTransition.Key))
        {
            transitionKeys.Add(workflow.StartTransition.Key);
        }

        // Cancel transition
        if (workflow.Cancel != null && !string.IsNullOrEmpty(workflow.Cancel.Key))
        {
            transitionKeys.Add(workflow.Cancel.Key);
        }

        // SharedTransitions
        foreach (var transition in workflow.SharedTransitions)
        {
            if (!string.IsNullOrEmpty(transition.Key))
            {
                transitionKeys.Add(transition.Key);
            }
        }

        // State transitions
        foreach (var state in workflow.States)
        {
            foreach (var transition in state.Transitions)
            {
                if (!string.IsNullOrEmpty(transition.Key))
                {
                    transitionKeys.Add(transition.Key);
                }
            }
        }

        return transitionKeys;
    }

    /// <summary>
    /// Validates a state's error boundary and its task error boundaries.
    /// </summary>
    private void ValidateStateErrorBoundary(
        State state,
        WorkflowValidationResult result,
        HashSet<string> stateKeys,
        HashSet<string> transitionKeys)
    {
        var stateContext = $"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}]";

        // Validate State ErrorBoundary
        if (state.ErrorBoundary != null)
        {
            var errors = _errorBoundaryValidator.Validate(
                state.ErrorBoundary,
                $"{stateContext}.{nameof(State.ErrorBoundary)}",
                stateKeys,
                transitionKeys);
            foreach (var error in errors)
            {
                result.AddError(error);
            }
        }

        // Validate OnEntry task ErrorBoundaries
        foreach (var (onEntry, index) in state.OnEntries.Select((t, i) => (t, i)))
        {
            if (onEntry.ErrorBoundary != null)
            {
                var errors = _errorBoundaryValidator.ValidateOnExecuteTaskBoundary(
                    onEntry,
                    $"{stateContext}.{nameof(State.OnEntries)}[{index}]",
                    stateKeys,
                    transitionKeys);
                foreach (var error in errors)
                {
                    result.AddError(error);
                }
            }
        }

        // Validate OnExit task ErrorBoundaries
        foreach (var (onExit, index) in state.OnExits.Select((t, i) => (t, i)))
        {
            if (onExit.ErrorBoundary != null)
            {
                var errors = _errorBoundaryValidator.ValidateOnExecuteTaskBoundary(
                    onExit,
                    $"{stateContext}.{nameof(State.OnExits)}[{index}]",
                    stateKeys,
                    transitionKeys);
                foreach (var error in errors)
                {
                    result.AddError(error);
                }
            }
        }

        // Validate SubFlow ErrorBoundary (if SubFlow has error handling configured)
        // Note: SubFlow error handling is defined but not implemented yet
    }

    /// <summary>
    /// Validates OnExecuteTask ErrorBoundaries in transitions.
    /// </summary>
    private void ValidateTransitionTaskErrorBoundaries(
        Workflow workflow,
        WorkflowValidationResult result,
        HashSet<string> stateKeys,
        HashSet<string> transitionKeys)
    {
        // Validate StartTransition OnExecute tasks
        if (workflow.StartTransition != null)
        {
            ValidateTransitionOnExecuteTasks(
                workflow.StartTransition,
                $"{nameof(Workflow)}.{nameof(Workflow.StartTransition)}",
                result,
                stateKeys,
                transitionKeys);
        }

        // Validate Cancel transition OnExecute tasks
        if (workflow.Cancel != null)
        {
            ValidateTransitionOnExecuteTasks(
                workflow.Cancel,
                $"{nameof(Workflow)}.{nameof(Workflow.Cancel)}",
                result,
                stateKeys,
                transitionKeys);
        }

        // Validate SharedTransitions OnExecute tasks
        foreach (var transition in workflow.SharedTransitions)
        {
            ValidateTransitionOnExecuteTasks(
                transition,
                $"{nameof(Workflow)}.{nameof(Workflow.SharedTransitions)}[{transition.Key}]",
                result,
                stateKeys,
                transitionKeys);
        }

        // Validate State transitions OnExecute tasks
        foreach (var state in workflow.States)
        {
            foreach (var transition in state.Transitions)
            {
                ValidateTransitionOnExecuteTasks(
                    transition,
                    $"{nameof(Workflow)}.{nameof(Workflow.States)}[{state.Key}].{nameof(State.Transitions)}[{transition.Key}]",
                    result,
                    stateKeys,
                    transitionKeys);
            }
        }
    }

    /// <summary>
    /// Validates OnExecuteTask ErrorBoundaries within a transition.
    /// </summary>
    private void ValidateTransitionOnExecuteTasks(
        Transition transition,
        string transitionContext,
        WorkflowValidationResult result,
        HashSet<string> stateKeys,
        HashSet<string> transitionKeys)
    {
        foreach (var (onExecute, index) in transition.OnExecutionTasks.Select((t, i) => (t, i)))
        {
            if (onExecute.ErrorBoundary != null)
            {
                var errors = _errorBoundaryValidator.ValidateOnExecuteTaskBoundary(
                    onExecute,
                    $"{transitionContext}.{nameof(Transition.OnExecutionTasks)}[{index}]",
                    stateKeys,
                    transitionKeys);
                foreach (var error in errors)
                {
                    result.AddError(error);
                }
            }
        }
    }

    #endregion
}
