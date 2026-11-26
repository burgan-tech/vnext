using BBT.Aether.Results;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Declarative;

/// <summary>
/// Where a declarative <see cref="BBT.Workflow.Definitions.MappingBlock"/> runs. The site decides the expression
/// variables (<see cref="Expressions.ScopeKind"/>), what a <c>select</c> reads and which paths a target may address
/// (<see cref="DeclarativeMappingPlan.Build"/>).
/// </summary>
public enum DeclarativeSiteKind
{
    /// <summary>A task binding's <c>input</c>: targets are task-config fields, <c>select</c> reads a variable.</summary>
    TaskInput,

    /// <summary>A task binding's <c>output</c>: targets are instance-data paths, <c>select</c> reads the task result (default <c>$.body</c>).</summary>
    TaskOutput,

    /// <summary>A transition's <c>output</c>: targets are instance-data paths, <c>select</c> reads a variable.</summary>
    TransitionOutput,

    /// <summary>
    /// A SubFlow's <c>input</c> (the child start request): targets start at <c>attributes</c>, <c>headers</c>,
    /// <c>key</c> or <c>tags</c> (the last two only as a whole), <c>select</c> reads a variable and needs <c>into</c>.
    /// </summary>
    SubFlowInput,

    /// <summary>
    /// A SubFlow's <c>output</c> (child completion): targets are instance-data paths, <c>select</c> reads the child's
    /// result document <c>{ data, status }</c> (default <c>$.data</c>).
    /// </summary>
    SubFlowOutput,

    /// <summary>
    /// An event's <c>input</c> (the event mapping): targets start at <c>key</c> (only as a whole) or <c>attributes</c>,
    /// <c>select</c> reads a variable (<c>input</c>, <c>now</c>, <c>context</c> — no <c>data</c>, <c>instance</c> or
    /// <c>user</c>) and needs <c>into</c>.
    /// </summary>
    EventInput,

    /// <summary>
    /// A workflow's <c>output</c> (the sync response): targets start at <c>data</c>, <c>statusCode</c> (only as a whole)
    /// or <c>headers</c>, <c>select</c> reads a variable and needs <c>into</c>.
    /// </summary>
    WorkflowOutput,

    /// <summary>
    /// A function's <c>output</c>: targets start at <c>data</c>, <c>statusCode</c> (only as a whole) or <c>headers</c>,
    /// <c>select</c> reads the result document <c>{ &lt;task&gt;: { body, headers, statusCode } }</c> (no default) and
    /// needs <c>into</c>.
    /// </summary>
    FunctionOutput
}

/// <summary>
/// Identifies one declarative block run for its failure message and its skip / failure logs.
/// </summary>
/// <param name="Name">
/// The owning component's key — the task, transition or SubFlow key — logged as the <c>TaskKey</c> of
/// <c>TaskDeclarativeMappingEntrySkipped</c>.
/// </param>
/// <param name="Phase">The block's phase as written in messages and logs (<c>input</c> / <c>output</c>).</param>
public sealed record DeclarativeSite(string Name, string Phase)
{
    /// <summary>The logger skip entries go to; null logs nothing.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// What a failure message names before <c>declarative {phase} mapping failed</c>; <see cref="Name"/> when null.
    /// A task binding passes <c>"{TaskType} task"</c>.
    /// </summary>
    public string? ErrorSubject { get; init; }

    /// <summary>
    /// The error code a failure of this site carries. <see cref="WorkflowErrorCodes.TaskExecution"/> by default (a task
    /// binding); a transition output passes <see cref="WorkflowErrorCodes.ExecutionStepFailed"/>, the code a failing
    /// transition mapping script returns. A caller-role provider failure (<c>Authorization:110006</c>) keeps its own code.
    /// </summary>
    public string ErrorCode { get; init; } = WorkflowErrorCodes.TaskExecution;

    /// <summary>The failure this site reports for <paramref name="detail"/>.</summary>
    internal Error Failure(string detail) =>
        Error.Failure(ErrorCode,
            $"{ErrorSubject ?? Name} declarative {Phase} mapping failed: {detail}");
}
