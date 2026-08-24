using System;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// Source-generated log messages for <see cref="ScriptSecretCache"/>.
/// Only store and bundle names are logged — never secret keys or values.
/// </summary>
internal static partial class ScriptSecretCacheLogs
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Secret bundle fetched from store {StoreName}, bundle {SecretStore} ({SecretCount} entries)")]
    public static partial void SecretBundleFetched(ILogger logger, string storeName, string secretStore, int secretCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Secret bundle fetch failed for store {StoreName}, bundle {SecretStore}")]
    public static partial void SecretBundleFetchFailed(ILogger logger, Exception exception, string storeName, string secretStore);
}
