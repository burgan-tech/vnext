using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BBT.Workflow.Data;

/// <summary>
/// Writes the context's schema into the query-text tag of the EF Core command span, in place of
/// Aether's schema placeholder.
/// </summary>
/// <remarks>
/// <para>
/// A context on Aether's placeholder model sends <c>__aether_schema__."Instances"</c> to EF Core, and
/// Aether's <c>QualifiedNamesCommandInterceptor</c> rewrites it to the real schema just before the command
/// runs. The OpenTelemetry EF Core instrumentation reads the command text when the command span starts,
/// which is before any interceptor. Without this interceptor every trace would therefore show the
/// placeholder instead of the schema, and anything downstream that reads <c>db.statement</c> would see
/// it too.
/// </para>
/// <para>
/// Only the tag the instrumentation already set is edited, and only its placeholder token is replaced.
/// That keeps the instrumentation's sanitising (literals as <c>?</c>), and the command text itself is
/// never touched. It runs in any position relative to Aether's interceptor, because it works on the tag,
/// not on the command. With no active span, or a tag that has no placeholder, it does nothing.
/// </para>
/// </remarks>
public sealed class SchemaPlaceholderStatementTagInterceptor(string schema) : DbCommandInterceptor
{
    // Old (db.statement) and stable (db.query.text) semantic-convention names; the instrumentation
    // emits either or both depending on OTEL_SEMCONV_STABILITY_OPT_IN.
    private static readonly string[] QueryTextTags = ["db.statement", "db.query.text"];

    // Npgsql quotes an identifier only when it has to, so the all-lowercase placeholder reaches the SQL
    // bare (`__aether_schema__."Instances"`); raw SQL or another provider may quote it. Both forms,
    // only in schema position (followed by a dot) and never inside a longer identifier.
    private static readonly Regex Placeholder = new(
        "(?<![\\w\"])(?:" + Regex.Escape(AetherSchemaModel.QuotedPlaceholder) + "|" + Regex.Escape(AetherSchemaModel.Placeholder) + ")(?=\\.)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly string _quotedSchema = "\"" + schema.Replace("\"", "\"\"") + "\"";

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        QualifyActivityTag();
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        QualifyActivityTag();
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        QualifyActivityTag();
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        QualifyActivityTag();
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        QualifyActivityTag();
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        QualifyActivityTag();
        return ValueTask.FromResult(result);
    }

    private void QualifyActivityTag()
    {
        var activity = Activity.Current;
        if (activity is null || !activity.IsAllDataRequested)
        {
            return;
        }

        foreach (var tag in QueryTextTags)
        {
            if (activity.GetTagItem(tag) is string text
                && text.Contains(AetherSchemaModel.Placeholder, StringComparison.Ordinal))
            {
                try
                {
                    activity.SetTag(tag, Placeholder.Replace(text, _quotedSchema));
                }
                catch (RegexMatchTimeoutException)
                {
                    // A span tag is never worth failing or stalling the command: keep the original text.
                }
            }
        }
    }
}
