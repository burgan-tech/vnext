namespace CustomScriptHelpersDemo.Contracts;

/// <summary>
/// Trimmed-down version of the runtime ScriptBase. Provides DI-injected
/// services to compiled scripts through property injection after instantiation.
/// In this demo it exposes config + logging helpers so the mapping script has
/// something from the base class to call alongside the custom helpers.
/// </summary>
public abstract class ScriptBase
{
    protected IScriptServices? Services { get; private set; }

    /// <summary>Injected by the engine right after the script is instantiated.</summary>
    public void SetServices(IScriptServices services)
        => Services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>Reads a configuration value (graceful default if missing).</summary>
    protected string GetConfig(string key, string defaultValue = "")
        => Services?.Configuration.TryGetValue(key, out var v) == true ? v : defaultValue;

    /// <summary>Writes an informational log line through the injected logger.</summary>
    protected void LogInformation(string message)
        => Services?.Log("INFO", $"{GetType().Name}: {message}");
}

/// <summary>Services injected into ScriptBase instances.</summary>
public interface IScriptServices
{
    IReadOnlyDictionary<string, string> Configuration { get; }
    void Log(string level, string message);
}

/// <summary>Console-backed implementation used by the demo host.</summary>
public sealed class DemoScriptServices(IReadOnlyDictionary<string, string> configuration) : IScriptServices
{
    public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

    public void Log(string level, string message)
        => Console.WriteLine($"      [{level}] {message}");
}
