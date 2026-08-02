using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Functions.Validation;

/// <summary>
/// Validates an incoming function request against the contract the function declares.
/// </summary>
public interface IFunctionRequestValidationService
{
    /// <summary>
    /// Validates the request body against the function's declared <c>inputSchema</c>.
    /// Returns success when the function declares no input schema or the request carries no body,
    /// so functions authored before contract declaration keep their current behaviour.
    /// </summary>
    /// <param name="function">The resolved function definition.</param>
    /// <param name="body">The request body, if any.</param>
    /// <param name="headers">Request headers, used to resolve the validation culture.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> ValidateRequestAsync(
        Function function,
        JsonElement? body,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken cancellationToken = default);
}
