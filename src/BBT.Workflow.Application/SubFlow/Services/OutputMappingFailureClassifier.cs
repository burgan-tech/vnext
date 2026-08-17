using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Decides whether a failure raised while applying a subflow output mapping is transient — the same
/// mapping would succeed on a later attempt — or permanent, meaning it can never succeed as written.
///
/// Transient is an ALLOWLIST. Anything unrecognised is permanent, which preserves the historical
/// behaviour and stops an unknown exception from becoming a poison message the broker redelivers
/// forever. Adding a type here is the intended maintenance point.
/// </summary>
internal static class OutputMappingFailureClassifier
{
    /// <summary>
    /// True when <paramref name="exception"/>, or any exception it wraps, is a known transient
    /// infrastructure fault. The inner chain is walked because script invocation and type
    /// initialisation both wrap the original fault. <see cref="ReflectionTypeLoadException"/> is
    /// unwrapped separately: its load faults live in <see cref="ReflectionTypeLoadException.LoaderExceptions"/>,
    /// not in <see cref="Exception.InnerException"/>, so the plain chain walk would otherwise miss it.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FileLoadException or BadImageFormatException)
            {
                return true;
            }

            if (current is ReflectionTypeLoadException loadFailure &&
                loadFailure.LoaderExceptions.Any(inner => inner is not null && IsTransient(inner)))
            {
                return true;
            }
        }

        return false;
    }
}
