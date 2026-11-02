namespace BBT.Workflow.Execution.Persistence;

/// <summary>
/// Runs a bookkeeping write — a task journal row, a transition record insert or completion — under
/// the commit policy of <c>WorkflowExecution:Bookkeeping</c>. With the default policy the write runs
/// exactly as before (autocommit). With <c>AsynchronousCommit</c> it runs inside a transaction that
/// commits with <c>synchronous_commit = off</c>, so the row is visible immediately but costs no WAL
/// fsync on the request path. An ambient transaction, when one is open, is always left alone.
/// </summary>
public interface IBookkeepingWriter
{
    /// <summary>
    /// Executes <paramref name="write"/> under the configured commit policy. The delegate performs
    /// the repository call(s) including their <c>SaveChanges</c>; the writer owns the surrounding
    /// transaction, if any.
    /// </summary>
    Task RunAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken = default);
}
