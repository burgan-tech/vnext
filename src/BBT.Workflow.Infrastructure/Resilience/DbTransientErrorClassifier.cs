using Npgsql;
using System.Net.Sockets;

namespace BBT.Workflow.Infrastructure.Resilience;

/// <summary>
/// Pure classifier that decides whether a database exception represents a genuinely
/// retriable transient fault at the application level.
///
/// <para>
/// EF Core's built-in <c>EnableRetryOnFailure</c> is incompatible with Aether's explicit
/// <c>BeginTransactionAsync</c> unit-of-work — hence retry policy is applied at the
/// application level and must be able to classify exceptions before Polly pipelines are
/// wired around DB operations.
/// </para>
///
/// <para>
/// Pool-exhaustion (<c>"pool has been exhausted"</c>) is NEVER retriable: retrying
/// amplifies the connection storm. This check takes precedence over all other rules.
/// </para>
/// </summary>
public static class DbTransientErrorClassifier
{
    /// <summary>
    /// Transient PostgreSQL <c>SqlState</c> codes that indicate a connection-level error
    /// that can be safely retried.
    /// </summary>
    private static readonly HashSet<string> TransientSqlStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "08000", // connection_exception
        "08003", // connection_does_not_exist
        "08006", // connection_failure
        "08001", // sqlclient_unable_to_establish_sqlconnection
        "08004", // sqlserver_rejected_establishment_of_sqlconnection
        "57P01", // admin_shutdown
        "57P03", // cannot_connect_now
    };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> (or any exception in its inner-exception
    /// chain) represents a retriable transient database fault.
    /// </summary>
    /// <remarks>
    /// <b>Rules (evaluated in order):</b>
    /// <list type="number">
    ///   <item>If ANY exception in the chain contains <c>"pool has been exhausted"</c> → <c>false</c> (never retry).</item>
    ///   <item>If any exception is an <see cref="NpgsqlException"/> with <c>IsTransient == true</c> → <c>true</c>.</item>
    ///   <item>If any exception is a <see cref="PostgresException"/> whose <c>SqlState</c> is in the transient set → <c>true</c>.</item>
    ///   <item>If any exception is a <see cref="SocketException"/> → <c>true</c>.</item>
    ///   <item>If any exception message contains <c>"Failed to connect"</c> → <c>true</c>.</item>
    ///   <item>Otherwise → <c>false</c>.</item>
    /// </list>
    /// </remarks>
    /// <param name="ex">The exception to classify. A <c>null</c> value returns <c>false</c>.</param>
    /// <returns><c>true</c> if the exception is a retriable transient fault; otherwise <c>false</c>.</returns>
    public static bool IsRetriableTransient(Exception? ex)
    {
        if (ex is null)
        {
            return false;
        }

        // Flatten the full exception chain (handles AggregateException and nested inner exceptions).
        var chain = FlattenChain(ex);

        // Rule 1 — pool-exhaustion is NEVER retriable; short-circuit immediately.
        if (chain.Any(e => ContainsPoolExhaustion(e.Message)))
        {
            return false;
        }

        // Rules 2-5 — evaluate remaining transience criteria.
        return chain.Any(IsTransientCandidate);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static bool ContainsPoolExhaustion(string? message)
    {
        return message is not null &&
               message.Contains("pool has been exhausted", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientCandidate(Exception e)
    {
        // Rule 2: NpgsqlException.IsTransient (driver-level transient flag)
        if (e is NpgsqlException { IsTransient: true })
        {
            return true;
        }

        // Rule 3: PostgresException with a known transient SqlState
        if (e is PostgresException pgEx && TransientSqlStates.Contains(pgEx.SqlState ?? string.Empty))
        {
            return true;
        }

        // Rule 4: SocketException (network layer failure)
        if (e is SocketException)
        {
            return true;
        }

        // Rule 5: message heuristic — covers PGBouncer / proxy "Failed to connect" messages
        if (e.Message.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Enumerates all exceptions in the chain: the root exception, every
    /// <see cref="Exception.InnerException"/>, and all leaves of any
    /// <see cref="AggregateException"/>.
    /// </summary>
    private static IEnumerable<Exception> FlattenChain(Exception root)
    {
        var stack = new Stack<Exception>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            if (current is AggregateException agg)
            {
                // AggregateException.Flatten() unwraps nested AggregateExceptions
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    stack.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                stack.Push(current.InnerException);
            }
        }
    }
}
