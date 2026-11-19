using BBT.Workflow.Middlewares;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.RuntimeCaller;

/// <summary>
/// Fails startup on a runtime-caller identity that would either break runtime calls (a required
/// ring that cannot be loaded) or expose its key (key material in configuration, or a key ring on
/// a host whose flow scripts run without the sandbox).
/// </summary>
internal sealed class RuntimeCallerOptionsValidator(IConfiguration configuration, bool runsFlowScripts)
    : IValidateOptions<RuntimeCallerOptions>
{
    private const int MinClockSkewSeconds = 60;
    private const int MaxClockSkewSeconds = 900;
    private const string SandboxEnabledKey = "Scripting:Sandbox:Enabled";

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(RuntimeCallerOptions.KeyFile),
        nameof(RuntimeCallerOptions.AppApiTokenFile),
        nameof(RuntimeCallerOptions.Required),
        nameof(RuntimeCallerOptions.MaxClockSkewSeconds),
        nameof(RuntimeCallerOptions.AllowWithoutScriptSandbox)
    };

    public ValidateOptionsResult Validate(string? name, RuntimeCallerOptions options)
    {
        var failures = new List<string>();

        if (options.MaxClockSkewSeconds is < MinClockSkewSeconds or > MaxClockSkewSeconds)
            failures.Add($"{RuntimeCallerOptions.SectionName}:MaxClockSkewSeconds ({options.MaxClockSkewSeconds}) " +
                         $"must be between {MinClockSkewSeconds} and {MaxClockSkewSeconds}.");

        AddKeyMaterialFailures(failures);
        AddKeyRingFailures(options, failures);

        var hasKeyFile = !string.IsNullOrWhiteSpace(options.KeyFile);
        var hasAppApiTokenFile = !string.IsNullOrWhiteSpace(options.AppApiTokenFile);
        if (hasAppApiTokenFile)
            AddLoadFailure(failures, () => DaprAppApiTokenLoader.Load(options.AppApiTokenFile));

        if ((hasKeyFile || hasAppApiTokenFile) && IsUnsandboxedScriptHost(options))
            failures.Add("A runtime-caller key file (KeyFile) or Dapr app API token file (AppApiTokenFile) is " +
                         $"configured on a host that runs flow scripts with {SandboxEnabledKey}=false: an " +
                         "unsandboxed script can read the mounted file. Enable the sandbox, or set " +
                         $"{RuntimeCallerOptions.SectionName}:AllowWithoutScriptSandbox=true " +
                         "(logged as Critical at every start).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void AddKeyMaterialFailures(List<string> failures)
    {
        var suspicious = configuration.GetSection(RuntimeCallerOptions.SectionName).GetChildren()
            .Select(child => child.Key)
            .Where(key => !KnownKeys.Contains(key) && LooksLikeKeyMaterial(key));

        foreach (var key in suspicious)
            failures.Add($"{RuntimeCallerOptions.SectionName}:{key} looks like key material. Keys belong " +
                         "in the mounted files named by KeyFile and AppApiTokenFile: flow scripts can read configuration.");
    }

    private static bool LooksLikeKeyMaterial(string key) =>
        key.Contains("key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static void AddKeyRingFailures(RuntimeCallerOptions options, List<string> failures)
    {
        if (!string.IsNullOrWhiteSpace(options.KeyFile))
            AddLoadFailure(failures, () => RuntimeCallerKeyRingLoader.Load(options.KeyFile));
        else if (options.Required)
            failures.Add($"{RuntimeCallerOptions.SectionName}:Required is true but no KeyFile is configured.");
    }

    private static void AddLoadFailure(List<string> failures, Action load)
    {
        try
        {
            load();
        }
        catch (InvalidOperationException exception)
        {
            failures.Add(exception.Message);
        }
    }

    private bool IsUnsandboxedScriptHost(RuntimeCallerOptions options) =>
        runsFlowScripts
        && !configuration.GetValue<bool>(SandboxEnabledKey)
        && !options.AllowWithoutScriptSandbox;
}
