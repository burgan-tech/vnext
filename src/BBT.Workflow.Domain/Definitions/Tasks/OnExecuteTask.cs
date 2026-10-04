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
        ScriptCode mapping,
        ErrorBoundary? errorBoundary,
        string? variableKey
    )
    {
        Order = order;
        Task = task;
        Mapping = mapping;
        ErrorBoundary = errorBoundary;
        VariableKey = variableKey;
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
    /// Optional mapping script for input/output transformation.
    /// </summary>
    public ScriptCode Mapping { get; private set; }
    
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
    public static OnExecuteTask Create(
        int order,
        IReference task,
        ScriptCode mapping,
        ErrorBoundary? errorBoundary = null,
        string? variableKey = null)
    {
        return new OnExecuteTask(
            order,
            task.ToReference(),
            mapping,
            errorBoundary,
            variableKey);
    }
}
