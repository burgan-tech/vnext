namespace BBT.Workflow.Execution.Transactions;

/// <summary>
/// Guardrail that enforces invariant INV-2 of the per-operation transactional model:
/// a transaction must never span a remote/external call. Callers invoke
/// <see cref="EnsureNoActiveTransaction"/> immediately before any remote operation (task
/// invocation, script eval, cross-service call). Behavior is governed by
/// <c>WorkflowExecutionOptions.TransactionGuardrailMode</c> (Off / Warn / Throw).
/// </summary>
public interface ITransactionGuardrail
{
    /// <summary>
    /// Verifies that no transactional unit of work is active in the ambient scope chain.
    /// Off: no-op. Warn: logs a warning + metric on violation. Throw: raises on violation.
    /// </summary>
    /// <param name="operation">A short label for the remote operation (for diagnostics).</param>
    void EnsureNoActiveTransaction(string operation);
}
