using System;
using System.IO;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Decides whether a failure raised while applying a subflow output mapping is transient — the same
/// mapping would succeed on a later attempt — or permanent, meaning it can never succeed as written.
///
/// The distinction is load-bearing. <see cref="SubflowCompletionService"/> turns a permanent failure
/// into a terminal outcome: it faults the parent and commits, in the same transaction that closed the
/// correlation, so nothing retries. Misclassifying an infrastructure fault as permanent destroys a
/// healthy instance.
///
/// Transient is an ALLOWLIST. Anything unrecognised is permanent, which preserves the historical
/// behaviour and stops an unknown exception from becoming a poison message the broker redelivers
/// forever. Adding a type here is the intended maintenance point.
/// </summary>
public static class OutputMappingFailureClassifier
{
    /// <summary>
    /// True when <paramref name="exception"/>, or any exception it wraps, is a known transient
    /// infrastructure fault. The inner chain is walked because script invocation and type
    /// initialisation both wrap the original fault.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FileLoadException or BadImageFormatException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }
}
