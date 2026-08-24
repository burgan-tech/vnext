using BBT.Workflow.Security;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Application.Security;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> decorator that strips the current instance's sensitive
/// values out of every log record it forwards.
/// <para>
/// This sits in front of the logger handed to <c>ScriptServices</c>, which is what every
/// <c>.csx</c> mapping's <c>LogTrace</c>/<c>LogInformation</c>/... call ends up writing to. That
/// is the single largest leak vector in the platform: the script-authoring surface hands authors
/// a structured logger and the documented example for it interpolates instance data. Decorating
/// the logger rather than the script base class means the protection cannot be bypassed by a new
/// logging helper, and the scripting module keeps its zero-dependency footprint.
/// </para>
/// <para>
/// Both halves of a log record are scrubbed — the rendered message AND the structured values,
/// because a sink reads the values directly and would otherwise persist the raw value under a
/// property name. When there is nothing to scrub the record is forwarded untouched, so the cost
/// on the common path is one flag check.
/// </para>
/// <para>
/// <b>Known gap:</b> an <see cref="Exception"/> attached to a record is forwarded as-is. Its
/// message can carry a sensitive value (a validation failure quoting the offending input), but
/// rewriting an arbitrary exception type is not safe. Call sites that build a diagnostic string
/// from an exception should scrub that string themselves before logging it.
/// </para>
/// </summary>
/// <typeparam name="TCategoryName">Logger category, preserved through the decoration.</typeparam>
/// <param name="inner">The logger to forward to.</param>
/// <param name="accessor">Supplies the scrubber for the work in flight.</param>
internal sealed class ScrubbingLogger<TCategoryName>(
    ILogger<TCategoryName> inner,
    ISensitiveDataScrubberAccessor accessor) : ILogger<TCategoryName>
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var scrubber = accessor.Current;

        if (scrubber.IsEmpty)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        var message = scrubber.Scrub(formatter(state, exception)) ?? string.Empty;

        // The message-template states produced by LoggerExtensions (FormattedLogValues) expose
        // their arguments through this interface. Rebuilding it keeps structured sinks working
        // while ensuring the values they read are the masked ones.
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            inner.Log(
                logLevel,
                eventId,
                ScrubbedLogValues.Create(values, scrubber, message),
                exception,
                static (scrubbed, _) => scrubbed.ToString());
            return;
        }

        inner.Log(logLevel, eventId, message, exception, static (text, _) => text);
    }
}

/// <summary>
/// A drop-in replacement for a message-template log state whose values have been scrubbed and
/// whose <see cref="ToString"/> yields the already-scrubbed message.
/// </summary>
internal sealed class ScrubbedLogValues : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly KeyValuePair<string, object?>[] _values;
    private readonly string _message;

    private ScrubbedLogValues(KeyValuePair<string, object?>[] values, string message)
    {
        _values = values;
        _message = message;
    }

    /// <summary>
    /// Copies the original values, scrubbing each one — including <c>{OriginalFormat}</c>, since a
    /// script that interpolated a value straight into its message template leaves the raw value in
    /// the template itself rather than in an argument.
    /// </summary>
    /// <param name="values">The original structured values.</param>
    /// <param name="scrubber">The scrubber to apply.</param>
    /// <param name="message">The already-scrubbed rendered message.</param>
    /// <returns>The substitute state.</returns>
    public static ScrubbedLogValues Create(
        IReadOnlyList<KeyValuePair<string, object?>> values,
        SensitiveDataScrubber scrubber,
        string message)
    {
        var scrubbed = new KeyValuePair<string, object?>[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var entry = values[i];
            scrubbed[i] = new KeyValuePair<string, object?>(entry.Key, scrubber.ScrubArgument(entry.Value));
        }

        return new ScrubbedLogValues(scrubbed, message);
    }

    /// <inheritdoc />
    public KeyValuePair<string, object?> this[int index] => _values[index];

    /// <inheritdoc />
    public int Count => _values.Length;

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Returns the scrubbed rendered message.</summary>
    public override string ToString() => _message;
}
