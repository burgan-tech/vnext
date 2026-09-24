using BBT.Aether.Guids;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Metrics;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Functions;

/// <summary>
/// The captured facts of one completed domain-function invocation, as
/// <c>FunctionAppService.ExecuteFunctionAsync</c> observed it.
/// </summary>
public sealed record FunctionExecutionRecord(
    string Domain,
    string FunctionKey,
    string FunctionVersion,
    TaskScope Scope,
    string? Workflow,
    Guid? InstanceId,
    DateTime InvokedAt,
    double DurationMs,
    bool Succeeded,
    int? StatusCode,
    string? ErrorCode,
    bool FromCache,
    string? TraceId = null);

/// <summary>
/// Best-effort writer for the function-execution journal (vnext-client-sdk-core#60, item C1). Records
/// one row per domain-function invocation; a write failure is logged and swallowed — journaling must
/// never fail the function it is recording.
/// </summary>
public interface IFunctionExecutionJournal
{
    /// <summary>Appends one execution row. Never throws.</summary>
    Task RecordAsync(FunctionExecutionRecord record, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class FunctionExecutionJournal(
    IFunctionExecutionRepository repository,
    IUnitOfWorkManager unitOfWorkManager,
    IGuidGenerator guidGenerator,
    ILogger<FunctionExecutionJournal> logger) : IFunctionExecutionJournal
{
    /// <inheritdoc />
    public async Task RecordAsync(FunctionExecutionRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            var execution = FunctionExecution.Record(
                guidGenerator.Create(),
                record.Domain,
                record.FunctionKey,
                record.FunctionVersion,
                record.Scope,
                record.Workflow,
                record.InstanceId,
                record.InvokedAt,
                record.DurationMs,
                record.Succeeded,
                record.StatusCode,
                record.ErrorCode,
                record.FromCache,
                record.TraceId);

            // Independent, non-transactional UoW: a function is a read and may carry no committing
            // ambient scope, so the journal row commits on its own (mirrors the InstanceTask journal
            // writer). CreatedBy/CreatedByBehalfOf are audit-stamped from the request ICurrentUser,
            // which is still ambient in this same DI scope.
            await using var uow = unitOfWorkManager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = false
            });
            await repository.InsertAsync(execution, cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort: never let a telemetry write failure surface to the function's caller.
            logger.FunctionExecutionJournalWriteFailed(ex, record.FunctionKey, record.Domain);
        }
    }
}
