using System;
using System.Net.Sockets;
using BBT.Workflow.Infrastructure.Resilience;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Resilience;

/// <summary>
/// Unit tests for <see cref="DbTransientErrorClassifier"/>.
/// </summary>
public class DbTransientErrorClassifierTests
{
    // -------------------------------------------------------------------------
    // Pool-exhaustion → always FALSE (highest precedence)
    // -------------------------------------------------------------------------

    [Fact]
    public void PoolExhaustion_ReturnsFalse()
    {
        var ex = new InvalidOperationException("The connection pool has been exhausted");
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeFalse();
    }

    [Fact]
    public void PoolExhaustion_WrappedInOuter_ReturnsFalse()
    {
        var inner = new InvalidOperationException("The connection pool has been exhausted, sorry");
        var outer = new Exception("A transient failure occurred", inner);
        DbTransientErrorClassifier.IsRetriableTransient(outer).ShouldBeFalse();
    }

    [Fact]
    public void PoolExhaustion_WinsOverTransientInnerException_ReturnsFalse()
    {
        // Chain has both a transient-looking SocketException AND a pool-exhaustion message.
        // Pool-exhaustion must win.
        var socket = new System.Net.Sockets.SocketException();
        var poolEx = new InvalidOperationException("pool has been exhausted", socket);
        DbTransientErrorClassifier.IsRetriableTransient(poolEx).ShouldBeFalse();
    }

    [Fact]
    public void PoolExhaustion_InAggregateException_ReturnsFalse()
    {
        var poolEx = new InvalidOperationException("Connection pool has been exhausted, max size reached");
        var agg = new AggregateException("Multiple errors", poolEx);
        DbTransientErrorClassifier.IsRetriableTransient(agg).ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Connect failures → TRUE
    // -------------------------------------------------------------------------

    [Fact]
    public void FailedToConnect_Message_ReturnsTrue()
    {
        var ex = new Exception("Failed to connect to 10.0.0.1:5432");
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeTrue();
    }

    [Fact]
    public void FailedToConnect_WrappedInOuter_ReturnsTrue()
    {
        var inner = new Exception("Failed to connect to db-host:5432 after 3 retries");
        var outer = new InvalidOperationException("Database operation failed", inner);
        DbTransientErrorClassifier.IsRetriableTransient(outer).ShouldBeTrue();
    }

    [Fact]
    public void InnerSocketException_ReturnsTrue()
    {
        var socket = new System.Net.Sockets.SocketException();
        var outer = new InvalidOperationException("DB error", socket);
        DbTransientErrorClassifier.IsRetriableTransient(outer).ShouldBeTrue();
    }

    [Fact]
    public void DirectSocketException_ReturnsTrue()
    {
        var ex = new System.Net.Sockets.SocketException();
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // NpgsqlException.IsTransient → TRUE  (real Npgsql type)
    // -------------------------------------------------------------------------

    [Fact]
    public void NpgsqlException_IsTransient_ReturnsTrue()
    {
        // Use a PostgresException with a transient SqlState (connection_failure = 08006).
        // PostgresException inherits NpgsqlException and sets IsTransient based on SqlState.
        var ex = CreatePostgresException("08006");
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeTrue();
    }

    [Fact]
    public void NpgsqlException_Wrapped_IsTransient_ReturnsTrue()
    {
        var npgsql = CreatePostgresException("08003"); // connection_does_not_exist
        var outer = new Exception("EF wrapped", npgsql);
        DbTransientErrorClassifier.IsRetriableTransient(outer).ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // PostgresException transient SqlStates → TRUE
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("08000")] // connection_exception
    [InlineData("08003")] // connection_does_not_exist
    [InlineData("08006")] // connection_failure
    [InlineData("08001")] // sqlclient_unable_to_establish_sqlconnection
    [InlineData("08004")] // sqlserver_rejected_establishment_of_sqlconnection
    [InlineData("57P01")] // admin_shutdown
    [InlineData("57P03")] // cannot_connect_now
    public void PostgresException_TransientSqlState_ReturnsTrue(string sqlState)
    {
        var ex = CreatePostgresException(sqlState);
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeTrue();
    }

    [Fact]
    public void PostgresException_NonTransientSqlState_ReturnsFalse()
    {
        // 23505 = unique_violation — not a connection/transient error
        var ex = CreatePostgresException("23505");
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Saturation signals → FALSE (retrying amplifies the connection storm)
    // -------------------------------------------------------------------------

    [Fact]
    public void PostgresException_TooManyConnections_53300_ReturnsFalse()
    {
        // 53300 = too_many_connections (PgBouncer / PostgreSQL server-side saturation).
        // Retrying this would amplify the connection storm — must never be retriable.
        var ex = CreatePostgresException("53300");
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeFalse();
    }

    [Fact]
    public void PostgresException_UndefinedTable_42P01_ReturnsFalse()
    {
        // 42P01 = undefined_table — a schema/logic error, not a transient fault.
        // Asserts that the transient set boundary is respected for unrelated SqlStates.
        var ex = CreatePostgresException("42P01");
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Unrelated exceptions → FALSE
    // -------------------------------------------------------------------------

    [Fact]
    public void GenericInvalidOperationException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("nope");
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeFalse();
    }

    [Fact]
    public void ArgumentException_ReturnsFalse()
    {
        var ex = new ArgumentException("bad argument");
        DbTransientErrorClassifier.IsRetriableTransient(ex).ShouldBeFalse();
    }

    [Fact]
    public void NullException_ReturnsFalse()
    {
        DbTransientErrorClassifier.IsRetriableTransient(null!).ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // AggregateException flattening → TRUE when inner is transient
    // -------------------------------------------------------------------------

    [Fact]
    public void AggregateException_WithTransientInner_ReturnsTrue()
    {
        var transient = CreatePostgresException("08006");
        var agg = new AggregateException("Multiple errors", transient);
        DbTransientErrorClassifier.IsRetriableTransient(agg).ShouldBeTrue();
    }

    [Fact]
    public void AggregateException_AllNonTransient_ReturnsFalse()
    {
        var agg = new AggregateException("Multiple errors",
            new InvalidOperationException("nope"),
            new ArgumentException("bad"));
        DbTransientErrorClassifier.IsRetriableTransient(agg).ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a <see cref="PostgresException"/> with the specified SqlState by using
    /// the public 4-parameter constructor: (messageText, severity, invariantSeverity, sqlState).
    /// </summary>
    private static PostgresException CreatePostgresException(string sqlState)
    {
        // Positional: (messageText, severity, invariantSeverity, sqlState)
        return new PostgresException($"PostgreSQL error {sqlState}", "ERROR", "ERROR", sqlState);
    }
}
