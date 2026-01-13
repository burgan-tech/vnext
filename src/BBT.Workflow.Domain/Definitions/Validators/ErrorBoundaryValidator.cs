using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates ErrorBoundary definitions including error rules, retry policies, and timeout policies.
/// Ensures error handling configurations are properly structured and valid.
/// </summary>
public sealed class ErrorBoundaryValidator
{
    /// <summary>
    /// Validates an ErrorBoundary definition.
    /// </summary>
    /// <param name="errorBoundary">The error boundary to validate.</param>
    /// <param name="context">The context path for error reporting (e.g., "Workflow.ErrorBoundary").</param>
    /// <param name="validStates">Set of valid state keys for timeout policy validation.</param>
    /// <param name="validTransitions">Set of valid transition keys for error handler transition validation.</param>
    /// <returns>A list of validation results.</returns>
    public IReadOnlyList<ValidationResult> Validate(
        ErrorBoundary? errorBoundary,
        string context,
        HashSet<string> validStates,
        HashSet<string>? validTransitions = null)
    {
        if (errorBoundary == null)
            return [];

        var results = new List<ValidationResult>();

        // Validate OnError rules
        if (errorBoundary.OnError.Count > 0)
        {
            for (var i = 0; i < errorBoundary.OnError.Count; i++)
            {
                var rule = errorBoundary.OnError[i];
                var ruleContext = $"{context}.OnError[{i}]";
                results.AddRange(ValidateErrorHandlerRule(rule, ruleContext, validStates, validTransitions));
            }

            // Check for duplicate priorities
            var priorities = errorBoundary.OnError
                .GroupBy(r => r.EffectivePriority)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (priorities.Count > 0)
            {
                results.Add(new ValidationResult(
                    $"Duplicate priorities found in error rules: {string.Join(", ", priorities)}. " +
                    "Consider assigning unique priorities for deterministic rule matching.",
                    [$"{context}.OnError"]));
            }
        }

        // Validate OnTimeout policy
        if (errorBoundary.OnTimeout != null)
        {
            results.AddRange(ValidateTimeoutPolicy(
                errorBoundary.OnTimeout,
                $"{context}.OnTimeout",
                validStates));
        }

        return results;
    }

    /// <summary>
    /// Validates a single ErrorHandlerRule.
    /// </summary>
    private IReadOnlyList<ValidationResult> ValidateErrorHandlerRule(
        ErrorHandlerRule rule,
        string context,
        HashSet<string> validStates,
        HashSet<string>? validTransitions)
    {
        var results = new List<ValidationResult>();

        // Validate Action is a valid enum
        if (!Enum.IsDefined(typeof(ErrorAction), rule.Action))
        {
            results.Add(new ValidationResult(
                $"Invalid ErrorAction value: {rule.Action}.",
                [$"{context}.{nameof(ErrorHandlerRule.Action)}"]));
        }

        // Validate Priority is positive
        if (rule.Priority <= 0)
        {
            results.Add(new ValidationResult(
                $"Priority must be greater than 0. Found: {rule.Priority}.",
                [$"{context}.{nameof(ErrorHandlerRule.Priority)}"]));
        }

        // Validate RetryPolicy is provided when Action is Retry
        if (rule.Action == ErrorAction.Retry && rule.RetryPolicy == null)
        {
            results.Add(new ValidationResult(
                "RetryPolicy is required when Action is Retry.",
                [$"{context}.{nameof(ErrorHandlerRule.RetryPolicy)}"]));
        }

        // Validate RetryPolicy configuration
        if (rule.RetryPolicy != null)
        {
            results.AddRange(ValidateRetryPolicy(
                rule.RetryPolicy,
                $"{context}.{nameof(ErrorHandlerRule.RetryPolicy)}"));
        }

        // Validate Transition references a valid transition key in the workflow
        if (!string.IsNullOrEmpty(rule.Transition) &&
            validTransitions != null &&
            !validTransitions.Contains(rule.Transition))
        {
            results.Add(new ValidationResult(
                $"Transition '{rule.Transition}' does not match any valid transition in the workflow.",
                [$"{context}.{nameof(ErrorHandlerRule.Transition)}"]));
        }

        // Validate Transition is required for Notify action (acts like Rollback)
        if (rule.Action == ErrorAction.Notify && string.IsNullOrEmpty(rule.Transition))
        {
            results.Add(new ValidationResult(
                "Transition is required when Action is Notify.",
                [$"{context}.{nameof(ErrorHandlerRule.Transition)}"]));
        }

        // Validate Transition is required for Rollback action
        if (rule.Action == ErrorAction.Rollback && string.IsNullOrEmpty(rule.Transition))
        {
            results.Add(new ValidationResult(
                "Transition is required when Action is Rollback.",
                [$"{context}.{nameof(ErrorHandlerRule.Transition)}"]));
        }

        // Validate ErrorTypes - should not contain empty strings
        if (rule.ErrorTypes != null)
        {
            for (var i = 0; i < rule.ErrorTypes.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(rule.ErrorTypes[i]))
                {
                    results.Add(new ValidationResult(
                        $"ErrorTypes contains empty value at index {i}.",
                        [$"{context}.{nameof(ErrorHandlerRule.ErrorTypes)}[{i}]"]));
                }
            }
        }

        // Validate ErrorCodes - should not contain empty strings (wildcards are valid)
        if (rule.ErrorCodes != null)
        {
            for (var i = 0; i < rule.ErrorCodes.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(rule.ErrorCodes[i]))
                {
                    results.Add(new ValidationResult(
                        $"ErrorCodes contains empty value at index {i}.",
                        [$"{context}.{nameof(ErrorHandlerRule.ErrorCodes)}[{i}]"]));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Validates a RetryPolicy configuration.
    /// </summary>
    private IReadOnlyList<ValidationResult> ValidateRetryPolicy(
        RetryPolicy policy,
        string context)
    {
        var results = new List<ValidationResult>();

        // MaxRetries must be non-negative
        if (policy.MaxRetries < 0)
        {
            results.Add(new ValidationResult(
                $"MaxRetries must be 0 or greater. Found: {policy.MaxRetries}.",
                [$"{context}.{nameof(RetryPolicy.MaxRetries)}"]));
        }

        // MaxRetries should have a reasonable upper bound
        if (policy.MaxRetries > 100)
        {
            results.Add(new ValidationResult(
                $"MaxRetries value {policy.MaxRetries} is unusually high. Consider a lower value.",
                [$"{context}.{nameof(RetryPolicy.MaxRetries)}"]));
        }

        // InitialDelay must be positive
        if (policy.InitialDelay <= TimeSpan.Zero)
        {
            results.Add(new ValidationResult(
                "InitialDelay must be greater than zero.",
                [$"{context}.{nameof(RetryPolicy.InitialDelay)}"]));
        }

        // MaxDelay must be greater than or equal to InitialDelay
        if (policy.MaxDelay < policy.InitialDelay)
        {
            results.Add(new ValidationResult(
                $"MaxDelay ({policy.MaxDelay}) must be greater than or equal to InitialDelay ({policy.InitialDelay}).",
                [$"{context}.{nameof(RetryPolicy.MaxDelay)}"]));
        }

        // BackoffMultiplier must be greater than 0 for exponential backoff
        if (policy.BackoffType == BackoffType.Exponential && policy.BackoffMultiplier <= 0)
        {
            results.Add(new ValidationResult(
                $"BackoffMultiplier must be greater than 0 for exponential backoff. Found: {policy.BackoffMultiplier}.",
                [$"{context}.{nameof(RetryPolicy.BackoffMultiplier)}"]));
        }

        // BackoffType must be a valid enum
        if (!Enum.IsDefined(typeof(BackoffType), policy.BackoffType))
        {
            results.Add(new ValidationResult(
                $"Invalid BackoffType value: {policy.BackoffType}.",
                [$"{context}.{nameof(RetryPolicy.BackoffType)}"]));
        }

        return results;
    }

    /// <summary>
    /// Validates a TimeoutPolicy configuration.
    /// </summary>
    private IReadOnlyList<ValidationResult> ValidateTimeoutPolicy(
        TimeoutPolicy policy,
        string context,
        HashSet<string> validStates)
    {
        var results = new List<ValidationResult>();

        // Validate Action is a valid enum
        if (!Enum.IsDefined(typeof(ErrorAction), policy.Action))
        {
            results.Add(new ValidationResult(
                $"Invalid ErrorAction value: {policy.Action}.",
                [$"{context}.{nameof(TimeoutPolicy.Action)}"]));
        }

        // Validate DefaultRetryPolicy when Action is Retry
        if (policy.Action == ErrorAction.Retry && policy.DefaultRetryPolicy == null)
        {
            results.Add(new ValidationResult(
                "DefaultRetryPolicy is required when Action is Retry.",
                [$"{context}.{nameof(TimeoutPolicy.DefaultRetryPolicy)}"]));
        }

        // Validate DefaultRetryPolicy configuration
        if (policy.DefaultRetryPolicy != null)
        {
            results.AddRange(ValidateRetryPolicy(
                policy.DefaultRetryPolicy,
                $"{context}.{nameof(TimeoutPolicy.DefaultRetryPolicy)}"));
        }

        // Validate Transition references a valid state
        if (!string.IsNullOrEmpty(policy.Transition) &&
            !validStates.Contains(policy.Transition))
        {
            results.Add(new ValidationResult(
                $"Transition '{policy.Transition}' does not match any valid state.",
                [$"{context}.{nameof(TimeoutPolicy.Transition)}"]));
        }

        return results;
    }

    /// <summary>
    /// Validates an OnExecuteTask's ErrorBoundary.
    /// </summary>
    /// <param name="onExecuteTask">The task execution configuration to validate.</param>
    /// <param name="context">The context path for error reporting.</param>
    /// <param name="validStates">Set of valid state keys for timeout policy validation.</param>
    /// <param name="validTransitions">Set of valid transition keys for error handler transition validation.</param>
    /// <returns>A list of validation results.</returns>
    public IReadOnlyList<ValidationResult> ValidateOnExecuteTaskBoundary(
        OnExecuteTask onExecuteTask,
        string context,
        HashSet<string> validStates,
        HashSet<string>? validTransitions = null)
    {
        if (onExecuteTask.ErrorBoundary == null)
            return [];

        return Validate(
            onExecuteTask.ErrorBoundary,
            $"{context}.ErrorBoundary",
            validStates,
            validTransitions);
    }
}

