using BBT.Aether.Users;
using BBT.Workflow.Instances.DTOs;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Context object passed to each <see cref="IInstanceFunctionHandler"/> during dispatch.
/// Carries all HTTP request data so handlers remain independent from the controller.
/// </summary>
public sealed record InstanceFunctionRequest(
    string Domain,
    string Workflow,
    string Instance,
    FunctionQueryParameters Parameters,
    string? IfNoneMatch,
    Dictionary<string, string?> Headers,
    Dictionary<string, string?> QueryParameters,
    ICurrentUser CurrentUser,
    HttpContext HttpContext);
