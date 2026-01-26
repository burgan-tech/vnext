namespace BBT.Workflow.Execution;

/// <summary>
/// Centralized constants for workflow HTTP client names used across all invokers.
/// </summary>
public static class WorkflowHttpClientNames
{
    /// <summary>
    /// Named HttpClient for SSL validation enabled requests (default behavior).
    /// </summary>
    public const string Default = "WorkflowHttpClient";

    /// <summary>
    /// Named HttpClient for SSL validation disabled requests.
    /// </summary>
    public const string NoSslValidation = "WorkflowHttpClient.NoSslValidation";
}
