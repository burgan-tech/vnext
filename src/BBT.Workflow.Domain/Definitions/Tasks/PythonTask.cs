using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Executes trusted Python code in the Execution service.
/// The script must expose <c>main(input)</c> and communicate through JSON values only.
/// </summary>
public sealed class PythonTask : WorkflowTask
{
    public const int DefaultTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 50;
    public const int MaxCodeBytes = 256 * 1024;
    public const int MaxInputBytes = 2 * 1024 * 1024;

    private PythonTask()
    {
    }

    [JsonConstructor]
    private PythonTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.Python).ToString();
    }

    /// <summary>
    /// Inline Python source. Native and Base64 encodings are supported.
    /// </summary>
    public ScriptCode? Script { get; private set; }

    /// <summary>
    /// Optional task-level runtime override: pythonNet, process, or container.
    /// </summary>
    public string? ExecutionMode { get; private set; }

    /// <summary>
    /// JSON input passed to <c>main(input)</c>.
    /// </summary>
    public JsonElement? Input { get; private set; }

    /// <summary>
    /// Invocation timeout. The remote invocation budget is larger than this value.
    /// </summary>
    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    public void SetScript(ScriptCode script) => Script = script;

    public void SetExecutionMode(string? executionMode) => ExecutionMode = executionMode;

    public void SetInput(dynamic? input) =>
        Input = JsonSerializer.SerializeToElement(input, JsonSerializerConstants.JsonOptions);

    public void SetTimeoutSeconds(int? timeoutSeconds) =>
        TimeoutSeconds = timeoutSeconds ?? DefaultTimeoutSeconds;

    internal void SetScriptInternal(ScriptCode? script) => Script = script;
    internal void SetExecutionModeInternal(string? executionMode) => ExecutionMode = executionMode;
    internal void SetInputInternal(JsonElement? input) => Input = input;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("script", out var script))
        {
            Script = script.Deserialize<ScriptCode>(JsonSerializerConstants.JsonOptions);
        }

        if (config.TryGetProperty("executionMode", out var executionMode) &&
            executionMode.ValueKind == JsonValueKind.String)
        {
            ExecutionMode = executionMode.GetString();
        }

        if (config.TryGetProperty("input", out var input))
        {
            Input = input.Clone();
        }

        if (config.TryGetProperty("timeoutSeconds", out var timeoutSeconds) &&
            timeoutSeconds.ValueKind == JsonValueKind.Number)
        {
            TimeoutSeconds = timeoutSeconds.GetInt32();
        }
    }

    public static PythonTask Create(JsonElement config) => new(config);

    public override WorkflowTask Clone() => CloneTyped();

    public PythonTask CloneTyped()
    {
        var cloned = new PythonTask();
        CopyBaseTo(cloned);
        cloned.Script = Script;
        cloned.ExecutionMode = ExecutionMode;
        cloned.Input = Input;
        cloned.TimeoutSeconds = TimeoutSeconds;
        return cloned;
    }

    public void CopyFromInternal(PythonTask source)
    {
        source.CopyBaseToInternal(this);
        SetScriptInternal(source.Script);
        SetExecutionModeInternal(source.ExecutionMode);
        SetInputInternal(source.Input);
        SetTimeoutSecondsInternal(source.TimeoutSeconds);
    }

    public override void Reset()
    {
        base.Reset();
        Script = null;
        ExecutionMode = null;
        Input = null;
        TimeoutSeconds = DefaultTimeoutSeconds;
    }

    public static PythonTask CreateEmpty() => new();
}
