using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Functions.Contracts;

/// <summary>
/// A <see cref="ScriptContext"/> that is built at most once per request, and only if something
/// actually needs it. Function contract resolution needs a script context only when an entry declares
/// a rule, and building one serializes the instance's full latest data - so a function that declares
/// no rules (the overwhelming majority) must never pay for it.
/// </summary>
/// <remarks>
/// Not thread-safe by design: a single request resolves its slots sequentially.
/// </remarks>
public sealed class LazyScriptContext(Func<CancellationToken, Task<ScriptContext>> factory)
{
    private readonly Func<CancellationToken, Task<ScriptContext>> factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    private ScriptContext? built;

    /// <summary>
    /// True when the context has already been materialized. Exposed so callers can reuse an
    /// already-built context without forcing a build.
    /// </summary>
    public bool IsMaterialized => built is not null;

    /// <summary>
    /// Returns the script context, building it on first access. A build failure is surfaced as a
    /// failed <see cref="Result{T}"/> rather than an exception so callers stay on the railway.
    /// </summary>
    /// <remarks>
    /// Caller cancellation propagates as <see cref="OperationCanceledException"/> instead of being
    /// converted to a failure: a client that hung up has not encountered an application error, and
    /// reporting one would log noise and mask the real outcome. An <c>OperationCanceledException</c>
    /// raised while our own token is still live (an internal timeout, say) is a genuine failure and
    /// does fall through to the <see cref="Result{T}"/> path.
    /// </remarks>
    public async Task<Result<ScriptContext>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (built is not null)
            return Result<ScriptContext>.Ok(built);

        try
        {
            built = await factory(cancellationToken);
            return Result<ScriptContext>.Ok(built);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<ScriptContext>.Fail(Error.Failure(
                WorkflowErrorCodes.ExtensionExecutionFailed,
                $"Script context could not be built for contract rule evaluation: {ex.Message}"));
        }
    }

    /// <summary>
    /// Wraps an already-built context, for callers that have one in hand.
    /// </summary>
    public static LazyScriptContext FromExisting(ScriptContext context)
    {
        var lazy = new LazyScriptContext(_ => Task.FromResult(context));
        lazy.built = context;
        return lazy;
    }
}
