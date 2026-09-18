using Microsoft.Extensions.Options;

namespace BBT.Workflow.Tasks.Invocation;

/// <summary>
/// Rejects an invocation mode the runtime cannot honour. <see cref="ExecutionMode.Custom"/>
/// exists in the shared enum for a plugin story that was never built; accepting it here would
/// silently behave as Remote, which is exactly the kind of "configured but inert" setting that
/// costs an outage to discover. Fail at boot instead.
/// </summary>
public sealed class TaskInvocationOptionsValidator : IValidateOptions<TaskInvocationOptions>
{
    public ValidateOptionsResult Validate(string? name, TaskInvocationOptions options)
    {
        var failures = new List<string>();

        if (options.DefaultMode == ExecutionMode.Custom)
            failures.Add($"{TaskInvocationOptions.SectionName}:DefaultMode cannot be 'Custom'.");

        foreach (var (taskType, mode) in options.Modes)
        {
            if (mode == ExecutionMode.Custom)
                failures.Add($"{TaskInvocationOptions.SectionName}:Modes:{taskType} cannot be 'Custom'.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
