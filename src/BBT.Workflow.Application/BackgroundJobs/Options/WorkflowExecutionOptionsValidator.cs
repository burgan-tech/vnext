using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs.Options;

/// <summary>
/// Enforces the retry/timeout budget hierarchy across configuration sources:
/// per-invocation timeout (<c>ExecutionApi:InvocationTimeoutSeconds</c>) ⊂ job execution
/// budget (<c>TransitionJobTimeoutSeconds</c>) ⊂ chain lock lease
/// (<c>TransitionLockLeaseSeconds</c> or its derived default). Each layer must fit inside
/// the next: a violated hierarchy silently re-enables the failure modes the budgets exist
/// to prevent (a task outliving its job, a chain outliving its lock), so misconfiguration
/// fails fast at first options resolution instead of surfacing as production race windows.
/// Also guards feature prerequisites: <c>EnableInstanceDataReconciliation</c> requires
/// <c>LatestOnlyInstanceLoading</c>.
/// </summary>
public sealed class WorkflowExecutionOptionsValidator(IConfiguration configuration)
    : IValidateOptions<WorkflowExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkflowExecutionOptions options)
    {
        var failures = new List<string>();

        var invocationTimeoutSeconds = int.TryParse(
            configuration["ExecutionApi:InvocationTimeoutSeconds"], out var parsed) ? parsed : 60;

        if (invocationTimeoutSeconds >= options.TransitionJobTimeoutSeconds)
        {
            failures.Add(
                $"ExecutionApi:InvocationTimeoutSeconds ({invocationTimeoutSeconds}s) must be smaller than " +
                $"WorkflowExecution:TransitionJobTimeoutSeconds ({options.TransitionJobTimeoutSeconds}s): " +
                "a single task invocation may not consume the whole job execution budget.");
        }

        var leaseSeconds = options.GetEffectiveLockLeaseSeconds();
        if (options.TransitionJobTimeoutSeconds >= leaseSeconds)
        {
            failures.Add(
                $"WorkflowExecution:TransitionJobTimeoutSeconds ({options.TransitionJobTimeoutSeconds}s) must be " +
                $"smaller than the chain lock lease ({leaseSeconds}s — TransitionLockLeaseSeconds or its derived " +
                "default): the lock must outlive the job budget and the timeout-recovery path.");
        }

        if (options.EnableInstanceDataReconciliation && !options.LatestOnlyInstanceLoading)
        {
            failures.Add(
                "WorkflowExecution:EnableInstanceDataReconciliation requires " +
                "WorkflowExecution:LatestOnlyInstanceLoading=true: reconciled data rows are synchronized " +
                "onto a partially loaded aggregate, so enabling reconciliation without latest-only " +
                "instance loading would fault every data-writing transition at runtime.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
