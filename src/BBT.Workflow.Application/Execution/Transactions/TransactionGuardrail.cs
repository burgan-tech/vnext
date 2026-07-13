using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Transactions;

/// <summary>
/// Default <see cref="ITransactionGuardrail"/>: walks the ambient unit-of-work chain
/// (<see cref="IUnitOfWorkManager.Current"/> and its <see cref="IUnitOfWork.Outer"/> ancestors)
/// and, if any non-completed unit of work is transactional, treats a remote call as an INV-2
/// violation. The mode is read per call from <c>WorkflowExecutionOptions</c> so it can be toggled
/// via configuration without a redeploy.
/// </summary>
public sealed class TransactionGuardrail(
    IUnitOfWorkManager uowManager,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<TransactionGuardrail> logger) : ITransactionGuardrail
{
    /// <inheritdoc />
    public void EnsureNoActiveTransaction(string operation)
    {
        var mode = executionOptions.Value.TransactionGuardrailMode;
        if (mode == TransactionGuardrailMode.Off)
            return;

        if (!IsTransactionalScopeActive())
            return;

        if (mode == TransactionGuardrailMode.Throw)
        {
            throw new InvalidOperationException(
                $"Transaction boundary violation (INV-2): remote operation '{operation}' was invoked " +
                "while a transactional unit of work is active. A transaction must not span a remote call " +
                "— it pins the pooled connection and holds row locks for the duration of the call. " +
                "Commit the current work unit before the remote call and open a new one afterwards.");
        }

        logger.TransactionGuardrailViolation(operation);
    }

    /// <summary>
    /// Returns true when any non-completed unit of work in the ambient chain is transactional.
    /// </summary>
    private bool IsTransactionalScopeActive()
    {
        var uow = uowManager.Current;
        while (uow is not null)
        {
            if (!uow.IsCompleted && uow.Options?.IsTransactional == true)
                return true;

            uow = uow.Outer;
        }

        return false;
    }
}
