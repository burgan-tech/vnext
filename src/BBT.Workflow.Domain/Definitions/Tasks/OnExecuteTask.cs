using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Represents a task to be executed during a transition lifecycle.
/// Can be used for OnEntry, OnExit, and OnExecute task configurations.
/// </summary>
public sealed class OnExecuteTask
{
    private const int VariableKeyMaxLength = 100;
    private static readonly Regex VariableKeyPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private OnExecuteTask()
    {
    }

    [JsonConstructor]
    private OnExecuteTask(
        int order,
        Reference task,
        ScriptCode? mapping,
        ErrorBoundary? errorBoundary,
        string? variableKey,
        MappingBlock? input,
        MappingBlock? output
    )
    {
        Order = order;
        Task = task;
        Mapping = mapping;
        ErrorBoundary = errorBoundary;
        VariableKey = variableKey;
        Input = input;
        Output = output;
    }

    /// <summary>
    /// The execution order of this task.
    /// </summary>
    public int Order { get; private set; }
    
    /// <summary>
    /// Reference to the task definition to execute.
    /// </summary>
    public Reference Task { get; private set; }
    
    /// <summary>
    /// Optional mapping script for input/output transformation. Null when the binding authors none
    /// (for example a binding that uses only the declarative <see cref="Input"/>/<see cref="Output"/> blocks).
    /// </summary>
    public ScriptCode? Mapping { get; private set; }
    
    /// <summary>
    /// Optional declarative input block: shapes the task request without a script.
    /// Absent on every binding authored before the declarative blocks existed.
    /// </summary>
    [JsonPropertyName("input")]
    public MappingBlock? Input { get; private set; }

    /// <summary>
    /// Optional declarative output block: files the task response without a script.
    /// Absent on every binding authored before the declarative blocks existed.
    /// </summary>
    [JsonPropertyName("output")]
    public MappingBlock? Output { get; private set; }

    /// <summary>True when the binding authors an <see cref="Input"/> or <see cref="Output"/> block.</summary>
    [JsonIgnore]
    public bool HasDeclarativeMapping => Input is not null || Output is not null;

    /// <summary>
    /// True when the declarative blocks are what runs: the binding authors a block and carries no script mapping code.
    /// A binding stored with both (only possible before publish rejected it) keeps its legacy meaning: the script runs and
    /// the blocks are ignored.
    /// </summary>
    [JsonIgnore]
    public bool RunsDeclarativeMapping => HasDeclarativeMapping && Mapping is not { HasMappingCode: true };

    /// <summary>
    /// Task-level error boundary.
    /// Provides the most specific error handling for this task.
    /// </summary>
    [JsonPropertyName("errorBoundary")]
    public ErrorBoundary? ErrorBoundary { get; private set; }

    /// <summary>
    /// Optional name of this entry's response slot in <c>ScriptContext.TaskResponse</c> /
    /// <c>OutputResponse</c>. Entries at the same order run in parallel and are merged by slot, so
    /// the same task listed twice at one order needs a distinct value here or the merge rejects the
    /// two different payloads. Used verbatim — not normalized — so a script reads exactly this name.
    /// </summary>
    [JsonPropertyName("variableKey")]
    public string? VariableKey { get; private set; }

    /// <summary>
    /// The slot this entry's response is filed under: <see cref="VariableKey"/> when authored, else
    /// the legacy <c>ToVariableName(task.key)</c>. Validators compare this value; the coordinator
    /// threads <see cref="VariableKey"/> into <c>TaskEngineExecutionOptions.ResponseVariableKey</c>.
    /// </summary>
    [JsonIgnore]
    public string? ResponseVariableKey =>
        !string.IsNullOrWhiteSpace(VariableKey) ? VariableKey : Task?.Key?.ToVariableName();

    /// <summary>True when <paramref name="value"/> is usable as a response slot name.</summary>
    public static bool IsValidVariableKey(string value) =>
        value.Length <= VariableKeyMaxLength && VariableKeyPattern.IsMatch(value);

    /// <summary>
    /// Creates a new OnExecuteTask instance.
    /// </summary>
    /// <param name="order">The execution order.</param>
    /// <param name="task">Reference to the task definition.</param>
    /// <param name="mapping">Optional mapping script.</param>
    /// <param name="errorBoundary">Optional task-level error boundary.</param>
    /// <param name="variableKey">Optional response slot name; defaults to the task key's variable name.</param>
    /// <param name="input">Optional declarative input block.</param>
    /// <param name="output">Optional declarative output block.</param>
    public static OnExecuteTask Create(
        int order,
        IReference task,
        ScriptCode? mapping,
        ErrorBoundary? errorBoundary = null,
        string? variableKey = null,
        MappingBlock? input = null,
        MappingBlock? output = null)
    {
        return new OnExecuteTask(
            order,
            task.ToReference(),
            mapping,
            errorBoundary,
            variableKey,
            input,
            output);
    }
}
