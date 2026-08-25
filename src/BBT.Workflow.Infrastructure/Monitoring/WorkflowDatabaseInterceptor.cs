using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Collections.Concurrent;
using System.Data.Common;

namespace BBT.Workflow.Monitoring;

/// <summary>
/// EF Core interceptor that automatically records database metrics for all operations.
/// This provides comprehensive database monitoring without requiring manual metric recording in each repository.
/// </summary>
public sealed class WorkflowDatabaseInterceptor : DbCommandInterceptor
{
    private readonly IWorkflowMetrics _workflowMetrics;

    /// <summary>
    /// Upper bound on the classification cache. EF reuses a small set of SQL texts per query shape,
    /// so the cache converges quickly; the cap only guards against unbounded growth from dynamic SQL
    /// (raw filter queries produce a new text per filter shape).
    /// </summary>
    private const int ClassificationCacheLimit = 2048;

    /// <summary>
    /// Query classification per SQL text. Classifying walks the (potentially multi-KB) command text,
    /// while every execution of the same query shape reuses the exact classification — so it is
    /// computed once per distinct text instead of twice per command (Executing + Executed).
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string QueryType, string TableName)> ClassificationCache = new();

    public WorkflowDatabaseInterceptor(IWorkflowMetrics workflowMetrics)
    {
        _workflowMetrics = workflowMetrics;
    }

    /// <summary>
    /// Intercepts command execution before it starts and records metrics
    /// </summary>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        var (queryType, tableName) = Classify(command);

        // Record query start
        _workflowMetrics.RecordDbQuery(queryType, tableName, "started");

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>
    /// Intercepts command execution after completion and records success metrics
    /// </summary>
    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        var (queryType, tableName) = Classify(command);

        // Record successful query
        _workflowMetrics.RecordDbQuery(queryType, tableName, "success");
        _workflowMetrics.RecordDbQueryDuration(queryType, tableName, eventData.Duration.TotalSeconds);

        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>
    /// Intercepts command execution failures and records error metrics
    /// </summary>
    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        var (queryType, tableName) = Classify(command);

        // Record failed query
        _workflowMetrics.RecordDbQuery(queryType, tableName, "error");
        _workflowMetrics.RecordDbError(queryType, tableName, eventData.Exception.GetType().Name);
        _workflowMetrics.RecordDbQueryDuration(queryType, tableName, eventData.Duration.TotalSeconds);

        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    /// <summary>
    /// Intercepts scalar command execution before it starts
    /// </summary>
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        var (queryType, tableName) = Classify(command);
        _workflowMetrics.RecordDbQuery(queryType, tableName, "started");

        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>
    /// Intercepts scalar command execution after completion
    /// </summary>
    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        var (queryType, tableName) = Classify(command);

        _workflowMetrics.RecordDbQuery(queryType, tableName, "success");
        _workflowMetrics.RecordDbQueryDuration(queryType, tableName, eventData.Duration.TotalSeconds);

        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>
    /// Intercepts non-query command execution before it starts
    /// </summary>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var (queryType, tableName) = Classify(command);
        _workflowMetrics.RecordDbQuery(queryType, tableName, "started");

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>
    /// Intercepts non-query command execution after completion
    /// </summary>
    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        var (queryType, tableName) = Classify(command);

        _workflowMetrics.RecordDbQuery(queryType, tableName, "success");
        _workflowMetrics.RecordDbQueryDuration(queryType, tableName, eventData.Duration.TotalSeconds);

        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    // Note: Transaction events require DbTransactionInterceptor, not DbCommandInterceptor
    // For now, we'll focus on command-level metrics. Transaction metrics can be added with a separate interceptor if needed.

    /// <summary>
    /// Resolves (query type, table name) metric labels for a command, serving repeats from the cache.
    /// </summary>
    private static (string QueryType, string TableName) Classify(DbCommand command)
    {
        var sql = command.CommandText ?? string.Empty;

        if (ClassificationCache.TryGetValue(sql, out var cached))
            return cached;

        (string, string) classification;
        try
        {
            var span = sql.AsSpan().Trim();
            var queryType = ExtractQueryType(span);
            classification = (queryType, ExtractTableName(span, queryType));
        }
        catch
        {
            // If parsing fails, return a safe default
            classification = ("Other", "Unknown");
        }

        if (ClassificationCache.Count < ClassificationCacheLimit)
            ClassificationCache.TryAdd(sql, classification);

        return classification;
    }

    /// <summary>
    /// Extracts the query type (SELECT, INSERT, UPDATE, DELETE) from SQL command text
    /// </summary>
    private static string ExtractQueryType(ReadOnlySpan<char> sql)
    {
        if (sql.IsEmpty)
            return "Unknown";

        if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return "SELECT";
        if (sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            return "INSERT";
        if (sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            return "UPDATE";
        if (sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            return "DELETE";
        if (sql.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
            return "CREATE";
        if (sql.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase))
            return "ALTER";
        if (sql.StartsWith("DROP", StringComparison.OrdinalIgnoreCase))
            return "DROP";

        return "Other";
    }

    /// <summary>
    /// Extracts the primary table name from SQL command text
    /// </summary>
    private static string ExtractTableName(ReadOnlySpan<char> sql, string queryType)
    {
        if (sql.IsEmpty)
            return "Unknown";

        return queryType switch
        {
            "SELECT" => ExtractTableAfterKeyword(sql, " FROM "),
            "INSERT" => ExtractTableAfterKeyword(sql, " INTO "),
            "UPDATE" => ExtractTableAfterKeyword(sql, "UPDATE "),
            "DELETE" => ExtractTableAfterKeyword(sql, " FROM "),
            _ => "Multiple"
        };
    }

    /// <summary>
    /// Returns the first identifier following <paramref name="keyword"/>, uppercased to keep the
    /// metric label values identical to the previous whole-text-uppercase implementation.
    /// </summary>
    private static string ExtractTableAfterKeyword(ReadOnlySpan<char> sql, ReadOnlySpan<char> keyword)
    {
        var keywordIndex = sql.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (keywordIndex == -1)
            return "Unknown";

        var afterKeyword = sql[(keywordIndex + keyword.Length)..].TrimStart();
        if (afterKeyword.IsEmpty)
            return "Unknown";

        // The identifier ends at the first whitespace or opening parenthesis (INSERT INTO t (...)).
        var end = 0;
        while (end < afterKeyword.Length && !char.IsWhiteSpace(afterKeyword[end]) && afterKeyword[end] != '(')
            end++;

        var token = afterKeyword[..end].Trim(['[', ']', '"', '`']);
        if (token.IsEmpty)
            return "Unknown";

        // Remove schema prefix if present
        var dotIndex = token.LastIndexOf('.');
        if (dotIndex > 0 && dotIndex < token.Length - 1)
            token = token[(dotIndex + 1)..].Trim(['[', ']', '"', '`']);

        return token.ToString().ToUpperInvariant();
    }
}
