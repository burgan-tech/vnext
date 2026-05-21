using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Functions;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Fallback handler for domain-level functions that don't have a specialized handler.
/// Delegates to <see cref="IFunctionAppService.GetFunctionByKeyAsync"/>.
/// </summary>
public sealed class DefaultDomainFunctionHandler(
    IFunctionAppService functionAppService) : IDomainFunctionHandler
{
    public string FunctionType => string.Empty;

    public async Task<IActionResult> HandleAsync(
        DomainFunctionRequest request, CancellationToken cancellationToken)
    {
        var result = await functionAppService.GetFunctionByKeyAsync(
            request.Function,
            request.Domain,
            request.Version,
            request.Headers,
            request.QueryParameters,
            request.Body,
            cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
