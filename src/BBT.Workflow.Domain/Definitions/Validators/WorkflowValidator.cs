using System.ComponentModel.DataAnnotations;

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

        // State level validations
        ValidateStateLabels(workflow, result);
        ValidateWizardStateTransitions(workflow, result);
        ValidateDefaultAutoTransitions(workflow, result);

        // Transition level validations
        ValidateTransitionLabels(workflow, result);
        ValidateTransitionRules(workflow, result);

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
        foreach (var availableState in workflow.StartTransition.AvailableIn)
        {
            if (!stateKeys.Contains(availableState))
            {
                result.AddError(new ValidationResult(
                    $"The 'availableIn' value in StartTransition does not match any state '{availableState}'.",
                    [$"{nameof(Workflow)}.{nameof(Workflow.StartTransition)}.{nameof(Transition.AvailableIn)}"]));
            }
        }

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
            foreach (var availableState in transition.AvailableIn)
            {
                if (!stateKeys.Contains(availableState))
                {
                    result.AddError(new ValidationResult(
                        $"The 'availableIn' value in shared transition '{transition.Key}' does not match any state '{availableState}'.",
                        [$"{nameof(Workflow)}.{nameof(Workflow.SharedTransitions)}[{transition.Key}].{nameof(Transition.AvailableIn)}"]));
                }
            }

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

        // Validate Cancel transition
        if (workflow.Cancel != null)
        {
            ValidateSingleTransition(workflow.Cancel, "Cancel", result, isSharedTransition: false);
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
        if (transition.Schema != null)
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
        if (transition.Schema != null)
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
