using System.Threading.Tasks;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Wraps read (HTTP GET) requests in a short <c>RequiresNew, IsTransactional=true</c> read-only
/// unit of work so their SELECTs run inside an active transaction. This is required under
/// <c>SchemaSwitchingMode.TransactionLocal</c> (pgbouncer transaction pooling in preprod/prod),
/// where every command — including reads — needs an active transaction for its
/// <c>SET LOCAL search_path</c>. The read app services do not open their own unit of work; they
/// rely on the ambient request unit of work, which the Aether middleware begins non-transactional
/// by default.
/// <para>
/// Opened as <see cref="UnitOfWorkScopeOption.RequiresNew"/> so it is independent of (nested under)
/// the ambient request unit and becomes the current unit for the read app services during
/// <c>next</c>. The ambient unit stays a lazy no-op envelope for GET requests. Write requests
/// (POST/PUT/PATCH/DELETE) are left untouched — the transition pipeline manages its own
/// per-operation transactional units, so this middleware never makes a write request span a
/// transaction across a remote call.
/// </para>
/// <para>
/// Gated by <see cref="WorkflowExecutionOptions.UseTransactionalReadScope"/> (default off): when
/// disabled the middleware is a pass-through and behavior is unchanged. Register after
/// <c>UseSchemaResolution()</c> and <c>UseAetherUnitOfWork()</c> so the current schema is resolved
/// before the read transaction opens.
/// </para>
/// </summary>
public sealed class ReadTransactionScopeMiddleware
{
    private static readonly UnitOfWorkOptions ReadScopeOptions = new()
    {
        IsTransactional = true,
        Scope = UnitOfWorkScopeOption.RequiresNew
    };

    private readonly RequestDelegate _next;
    private readonly bool _enabled;

    /// <summary>
    /// Initializes the middleware, caching the enable flag (options are singleton; the unit-of-work
    /// manager is resolved per request in <see cref="InvokeAsync"/> because it is scoped).
    /// </summary>
    public ReadTransactionScopeMiddleware(RequestDelegate next, IOptions<WorkflowExecutionOptions> options)
    {
        _next = next;
        _enabled = options.Value.UseTransactionalReadScope;
    }

    /// <summary>
    /// Opens a read-only transactional unit of work around the request when enabled and the request
    /// is a GET; otherwise passes through unchanged.
    /// </summary>
    public async Task InvokeAsync(HttpContext context, IUnitOfWorkManager uowManager)
    {
        if (!_enabled || !HttpMethods.IsGet(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // Commits on success; disposal rolls back on exception (harmless for a read-only unit).
        await uowManager.ExecuteInUowAsync(
            async _ => await _next(context),
            ReadScopeOptions,
            context.RequestAborted);
    }
}
