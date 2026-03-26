using BBT.Aether.Application;
using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Functions;

/// <summary>
/// Application service for function operations.
/// All methods return Result types following Railway pattern.
/// </summary>
public interface IFunctionAppService : IApplicationService
{
    /// <summary>
    /// Gets function data by function key.
    /// <param name="key">Function Key</param>
    /// <param name="domain">Domain Name</param>
    /// <param name="version">Request Version</param>
    /// <param name="headers">Request Headers</param>
    /// <param name="queryParameters">Request Query Params</param>
    /// <param name="cancellationToken">Cancellation Token</param>
    Task<Result<Dictionary<string, dynamic?>>> GetFunctionByKeyAsync(
        string key,
        string domain,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets function data by instance key or ID.
    /// </summary>
    /// <param name="key">Function Key</param>
    /// <param name="flow">Workflow Key</param>
    /// <param name="domain">Domain Name</param>
    /// <param name="instance">Instance Identifier</param>
    /// <param name="headers">Request Headers</param>
    /// <param name="queryParameters">Request Query Params</param>
    /// <param name="cancellationToken">Cancellation Token</param>
    Task<Result<Dictionary<string, dynamic?>>> GetFunctionByInstanceAsync(
        string key,
        string flow,
        string domain,
        string instance,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active domain functions.
    /// </summary>
    /// <param name="domain">Domain Name</param>
    /// <param name="cancellationToken">Cancellation Token</param>
    Task<Result<List<InstanceAndDataModel>>> GetFunctionsAsync(
        string domain,
        CancellationToken cancellationToken = default);
}