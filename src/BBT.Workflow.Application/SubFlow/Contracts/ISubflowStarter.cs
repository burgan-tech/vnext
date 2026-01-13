using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Interface for service managing SubFlow and SubProcess workflows.
/// This interface defines the contract for handling execution and correlation management
/// between parent workflow and SubFlow definitions. Both SubFlow and SubProcess now create separate instances.
/// </summary>
public interface ISubflowStarter
{
    /// <summary>
    /// Handles the initiation of SubFlow execution.
    /// Both SubFlow and SubProcess now create separate instances via remote calls.
    /// </summary>
    /// <param name="workflow">The main workflow.</param>
    /// <param name="parentInstance">The main workflow instance that initiates the sub-flow.</param>
    /// <param name="targetState">The target state containing SubFlow configuration.</param>
    /// <param name="transition">The current transition.</param>
    /// <param name="correlation">Correlation information for tracking.</param>
    /// <param name="context">The script context containing execution data and headers.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>Result indicating success or failure of the sub-flow initiation.</returns>
    /// <remarks>
    /// <para>
    /// SubFlow (Type: "S"): Creates a separate instance and blocks the parent workflow until completion. 
    /// The parent workflow cannot continue until the SubFlow is completed.
    /// </para>
    /// <para>
    /// SubProcess (Type: "P"): Creates a separate instance and runs in parallel without blocking the parent workflow. 
    /// The parent workflow can continue immediately after starting the SubProcess.
    /// </para>
    /// </remarks>
    Task<Result> StartAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        State targetState,
        Transition transition,
        InstanceCorrelation correlation,
        ScriptContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a SubProcess workflow without requiring a target state or mapping.
    /// Used for triggering SubProcess workflows from tasks.
    /// </summary>
    /// <param name="workflow">The parent workflow.</param>
    /// <param name="parentInstance">The parent instance.</param>
    /// <param name="subFlowReference">Reference to the SubFlow/SubProcess to start.</param>
    /// <param name="transition">The transition triggering the SubProcess.</param>
    /// <param name="correlation">Correlation information for tracking.</param>
    /// <param name="subFlowType">Type code of the SubFlow ("S" or "P").</param>
    /// <param name="inputMappingResult">Optional input mapping result containing data, headers, and key information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success or failure of the sub-flow initiation.</returns>
    Task<Result> SubStartAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        Reference subFlowReference,
        Transition transition,
        InstanceCorrelation correlation,
        string subFlowType,
        ScriptResponse? inputMappingResult = null,
        CancellationToken cancellationToken = default);
}