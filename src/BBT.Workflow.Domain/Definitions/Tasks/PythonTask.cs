using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Calls a named handler in the domain's own Python runtime container. The Orchestration host
/// invokes the runtime directly over Dapr service invocation with the versioned Python runtime
/// contract; the code and its packages live in the container image, never in the task definition.
/// Input and output go through the standard task mapping (<c>InputHandler</c> sets
/// <see cref="Input"/> with <see cref="SetInput"/>, <c>OutputHandler</c> reads the handler output).
/// </summary>
public sealed class PythonTask : WorkflowTask
{
    public const int DefaultTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 50;
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
    /// Name of the handler registered in the Python runtime container.
    /// </summary>
    public string? Handler { get; private set; }

    /// <summary>
    /// JSON input passed to the handler. Seeded from <c>config.input</c>; an input mapping may replace it.
    /// </summary>
    public JsonElement? Input { get; private set; }

    /// <summary>
    /// Handler timeout sent to the runtime; the Orchestration-side call budget is slightly larger.
    /// </summary>
    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    public void SetHandler(string? handler) => Handler = handler;

    public void SetInput(dynamic? input) =>
        Input = JsonSerializer.SerializeToElement(input, JsonSerializerConstants.JsonOptions);

    public void SetTimeoutSeconds(int? timeoutSeconds) =>
        TimeoutSeconds = timeoutSeconds ?? DefaultTimeoutSeconds;

    internal void SetHandlerInternal(string? handler) => Handler = handler;
    internal void SetInputInternal(JsonElement? input) => Input = input;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("handler", out var handler) &&
            handler.ValueKind == JsonValueKind.String)
        {
            Handler = handler.GetString();
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
        cloned.Handler = Handler;
        cloned.Input = Input;
        cloned.TimeoutSeconds = TimeoutSeconds;
        return cloned;
    }

    public void CopyFromInternal(PythonTask source)
    {
        source.CopyBaseToInternal(this);
        SetHandlerInternal(source.Handler);
        SetInputInternal(source.Input);
        SetTimeoutSecondsInternal(source.TimeoutSeconds);
    }

    public override void Reset()
    {
        base.Reset();
        Handler = null;
        Input = null;
        TimeoutSeconds = DefaultTimeoutSeconds;
    }

    public static PythonTask CreateEmpty() => new();
}
