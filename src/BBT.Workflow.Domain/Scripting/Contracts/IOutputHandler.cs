namespace BBT.Workflow.Scripting;

/// <summary>
/// Defines the contract for function output mapping in workflow execution.
/// Implement this interface in a C# script assigned to a Function's <c>output</c> field.
/// The handler runs after all tasks in the function complete and maps their results
/// (available via <see cref="ScriptContext.OutputResponse"/> and <see cref="ScriptContext.TaskResponse"/>)
/// to the final function response.
/// </summary>
public interface IOutputHandler
{
    /// <summary>
    /// Handles output data mapping after all function tasks have executed.
    /// Read task results from <paramref name="context"/> and return the mapped response.
    /// </summary>
    /// <param name="context">
    /// The script context containing all task execution results, instance data, headers,
    /// and other contextual information. Use <c>context.OutputResponse</c> or
    /// <c>context.TaskResponse</c> to access individual task outputs.
    /// </param>
    /// <returns>
    /// A <see cref="ScriptResponse"/> whose <c>Data</c> property becomes the function's response payload.
    /// </returns>
    Task<ScriptResponse> OutputHandler(ScriptContext context);
}
