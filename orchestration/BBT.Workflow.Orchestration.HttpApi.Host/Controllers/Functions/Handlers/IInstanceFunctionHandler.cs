using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Strategy for handling a specific system function type within a workflow instance context.
/// Each implementation handles exactly one <see cref="FunctionType"/> and produces the full HTTP response.
/// </summary>
/// <remarks>
/// Register all implementations with DI as <c>IEnumerable&lt;IInstanceFunctionHandler&gt;</c>.
/// The <see cref="IInstanceFunctionHandlerFactory"/> resolves the correct handler at runtime.
/// </remarks>
public interface IInstanceFunctionHandler
{
    /// <summary>
    /// The system function key this handler is responsible for (matches a <c>FunctionTypeConst</c> constant).
    /// </summary>
    string FunctionType { get; }

    /// <summary>
    /// Handles the function request and returns the full HTTP response.
    /// </summary>
    Task<IActionResult> HandleAsync(InstanceFunctionRequest request, CancellationToken cancellationToken);
}
