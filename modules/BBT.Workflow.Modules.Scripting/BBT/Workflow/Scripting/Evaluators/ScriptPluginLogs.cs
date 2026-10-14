using System;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// Source-generated log messages for <see cref="ScriptPluginLoadContext"/>. This module does not
/// reference the Domain, so it cannot use <c>WorkflowLogs</c>; the ids continue that file's script block
/// (600xx) and are reserved there, so a log pipeline never sees two events under one number.
/// </summary>
internal static partial class ScriptPluginLogs
{
    [LoggerMessage(
        EventId = 60017,
        Level = LogLevel.Warning,
        Message = "Script plugin {AssemblyName} could not be loaded from {PluginPath}; falling back to the host's resolution")]
    public static partial void ScriptPluginLoadFailed(ILogger logger, Exception exception, string assemblyName, string pluginPath);

    [LoggerMessage(
        EventId = 60018,
        Level = LogLevel.Warning,
        Message = "Script plugin {AssemblyName} could not be resolved from the plugin context; falling back to the host's resolution")]
    public static partial void ScriptPluginResolveFailed(ILogger logger, Exception exception, string assemblyName);
}
