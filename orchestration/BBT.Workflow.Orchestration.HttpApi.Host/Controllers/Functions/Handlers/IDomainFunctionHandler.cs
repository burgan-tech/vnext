using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Strategy for handling a specific function type at domain level (without instance context).
/// Each implementation handles exactly one function key and produces the full HTTP response.
/// </summary>
public interface IDomainFunctionHandler
{
    /// <summary>
    /// The function key this handler is responsible for.
    /// </summary>
    string FunctionType { get; }

    /// <summary>
    /// Handles the function request and returns the full HTTP response.
    /// </summary>
    Task<IActionResult> HandleAsync(DomainFunctionRequest request, CancellationToken cancellationToken);
}
