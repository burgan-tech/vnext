using BBT.Aether.Results;
using BBT.Workflow.Functions.DTOs;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Functions;

/// <summary>
/// Function discovery: describes a function to a client that is about to call it, and serves the
/// view and schema contracts that description links to.
/// </summary>
/// <remarks>
/// Every method enforces the same scope and role gates as execution (<see cref="IFunctionAccessPolicy"/>),
/// so a caller who cannot invoke the function cannot learn its shape either.
/// Only custom (<c>sys-functions</c>) functions are describable; built-in system functions such as
/// <c>state</c> or <c>view</c> have no component definition and resolve to not-found.
/// </remarks>
public interface IFunctionInfoAppService
{
    /// <summary>
    /// Describes a domain-scoped function.
    /// </summary>
    Task<Result<FunctionInfoOutput>> GetInfoByKeyAsync(
        string domain,
        string key,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes a function in the context of a workflow instance. Serves functions of every scope,
    /// because an instance satisfies Domain, Flow and Instance alike.
    /// </summary>
    Task<Result<FunctionInfoOutput>> GetInfoByInstanceAsync(
        string domain,
        string workflow,
        string instanceKey,
        string key,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the functions the instance's workflow declares, each linked to its <c>info</c> endpoint.
    /// Backs the <c>catalog</c> system function the state response points at.
    /// </summary>
    /// <remarks>
    /// Role-filtered: a function the caller could not invoke is omitted, so every returned link is
    /// actionable. Declaration order is preserved.
    /// </remarks>
    Task<Result<FunctionCatalogOutput>> GetCatalogByInstanceAsync(
        string domain,
        string workflow,
        string instanceKey,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves and returns the content of a domain-scoped function's input or output view.
    /// The <c>target</c> selects which declared view to resolve - <c>input</c> or <c>output</c>.
    /// </summary>
    Task<Result<GetViewOutput>> GetViewByKeyAsync(
        string domain,
        string key,
        string target,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves and returns the content of an instance-bound function's input or output view.
    /// The <c>target</c> selects which declared view to resolve - <c>input</c> or <c>output</c>.
    /// </summary>
    Task<Result<GetViewOutput>> GetViewByInstanceAsync(
        string domain,
        string workflow,
        string instanceKey,
        string key,
        string target,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves and returns a domain-scoped function's input or output schema document.
    /// The <c>target</c> selects which declared schema to resolve - <c>input</c> or <c>output</c>.
    /// </summary>
    Task<Result<FunctionSchemaOutput>> GetSchemaByKeyAsync(
        string domain,
        string key,
        string target,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves and returns an instance-bound function's input or output schema document.
    /// The <c>target</c> selects which declared schema to resolve - <c>input</c> or <c>output</c>.
    /// </summary>
    Task<Result<FunctionSchemaOutput>> GetSchemaByInstanceAsync(
        string domain,
        string workflow,
        string instanceKey,
        string key,
        string target,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default);
}
