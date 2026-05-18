using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Context object passed to each <see cref="IDomainFunctionHandler"/> during dispatch.
/// Carries all HTTP request data for domain-level function lookups (no instance context).
/// </summary>
public sealed record DomainFunctionRequest(
    string Domain,
    string Function,
    string? Version,
    Dictionary<string, string?> Headers,
    Dictionary<string, string?> QueryParameters,
    HttpContext HttpContext);
