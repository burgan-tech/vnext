using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Data;

/// <inheritdoc cref="IBookkeepingWriter" />
/// <remarks>
/// <c>SET LOCAL synchronous_commit = off</c> is transaction-scoped, so it is PgBouncer
/// transaction-mode safe and can never leak into the next statement on the connection. An
/// asynchronously committed transaction is still made durable by the next synchronous commit on
/// the same connection (the WAL is flushed up to that commit's LSN), so the exposure is bounded by
/// <c>wal_writer_delay</c> and by the instance's next state or data write, whichever comes first.
/// </remarks>
public sealed class BookkeepingWriter(
    IAetherDbContextProvider<WorkflowDbContext> dbContextProvider,
    IOptions<WorkflowExecutionOptions> executionOptions) : IBookkeepingWriter
{
    /// <inheritdoc />
    public async Task RunAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        if (!executionOptions.Value.Bookkeeping.AsynchronousCommit)
        {
            await write(cancellationToken);
            return;
        }

        var context = await dbContextProvider.GetDbContextAsync();

        // Inside someone else's transaction the commit policy is theirs — do not change it.
        if (context.Database.CurrentTransaction is not null)
        {
            await write(cancellationToken);
            return;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit = off", cancellationToken);
        await write(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
