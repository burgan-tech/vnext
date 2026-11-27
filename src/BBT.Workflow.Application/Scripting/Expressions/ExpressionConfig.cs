using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>
/// The <c>config</c> variable of a declarative mapping expression: read-only access to the host's
/// <see cref="IConfiguration"/>, the same source and reach as a C# mapping's <c>GetConfigValue(key)</c>
/// (<c>ScriptBase</c>). Member names are lowercase because expressions spell them that way:
/// <c>config.get("Example:ApiBaseUrl")</c>, <c>config.get("Example:ApiBaseUrl", "http://localhost:3001")</c>.
/// </summary>
/// <remarks>
/// Like <c>GetConfigValue</c> it can read any key, connection strings included; restricting what a domain's
/// expressions may read is a separate, domain-owner decision (not this variable's).
/// </remarks>
#pragma warning disable IDE1006 // lowercase members are the expression-language surface
public sealed class ExpressionConfig
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    private readonly IConfiguration _configuration;

    internal ExpressionConfig(IConfiguration? configuration) => _configuration = configuration ?? EmptyConfiguration;

    /// <summary>The value at <paramref name="key"/> (<c>:</c>-separated), or null when it is not set.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null or blank.</exception>
    public string? get(string key) => _configuration[Checked(key)];

    /// <summary>The value at <paramref name="key"/>, or <paramref name="fallback"/> when it is not set.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null or blank.</exception>
    public string get(string key, string fallback) => _configuration[Checked(key)] ?? fallback;

    private static string Checked(string key) =>
        string.IsNullOrWhiteSpace(key)
            ? throw new ArgumentException("Configuration key cannot be null or empty", nameof(key))
            : key;
}
#pragma warning restore IDE1006
