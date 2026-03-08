using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Instances.DTOs;

namespace BBT.Workflow.Instances;

/// <summary>
/// Resolves view content by domain: local (component cache) or remote (GetInstanceAsync), and maps to GetViewOutput.
/// Single place for view-by-reference resolution and mapping, improving maintainability and extensibility.
/// </summary>
public interface IViewContentResolutionService
{
    /// <summary>
    /// Resolves view content for the given view reference and request domain.
    /// When view reference domain equals request domain, loads from local component cache.
    /// When different, fetches via remote GetInstanceAsync and maps Attributes to GetViewOutput.
    /// </summary>
    /// <param name="viewRef">View reference (key, domain, flow, version).</param>
    /// <param name="requestDomain">The request/current domain.</param>
    /// <param name="headers">Optional request headers (auth/tenant propagation for remote).</param>
    /// <param name="queryParameters">Optional query parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with GetViewOutput, or failure.</returns>
    Task<Result<GetViewOutput>> ResolveViewContentAsync(
        IReference viewRef,
        string requestDomain,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken = default);
}
