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

        foreach (var child in configuration.GetSection(RuntimeCallerOptions.SectionName).GetChildren())
        {
            if (!KnownKeys.Contains(child.Key)
                && (child.Key.Contains("key", StringComparison.OrdinalIgnoreCase)
                    || child.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)))
                failures.Add($"{RuntimeCallerOptions.SectionName}:{child.Key} looks like key material. Keys belong " +
                             "in the mounted key file named by KeyFile: flow scripts can read configuration.");
        }

        if (string.IsNullOrWhiteSpace(options.KeyFile))
        {
            if (options.Required)
                failures.Add($"{RuntimeCallerOptions.SectionName}:Required is true but no KeyFile is configured.");
        }
        else
        {
            try
            {
                RuntimeCallerKeyRingLoader.Load(options.KeyFile);
            }
            catch (InvalidOperationException exception)
            {
                failures.Add(exception.Message);
            }

            if (runsFlowScripts
                && !configuration.GetValue<bool>(SandboxEnabledKey)
                && !options.AllowWithoutScriptSandbox)
                failures.Add("A runtime-caller key ring is configured on a host that runs flow scripts with " +
                             $"{SandboxEnabledKey}=false: an unsandboxed script can read the key file. Enable the " +
                             $"sandbox, or set {RuntimeCallerOptions.SectionName}:AllowWithoutScriptSandbox=true " +
                             "(logged as Critical at every start).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
