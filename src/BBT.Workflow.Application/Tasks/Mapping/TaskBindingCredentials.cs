using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Adds the caller's credential headers to a typed binding — the typed twin of
/// <c>TaskExecutorBase.WithCallerCredential(TaskEnvelope, …)</c>, using the same merge
/// (<see cref="HttpTaskInvocation.WithCallerCredential"/>) on the binding's <c>Headers</c> JSON string.
/// Returns the SAME instance when the merge leaves the headers unchanged, otherwise a copy that differs
/// from the original only in <c>Headers</c>.
/// </summary>
/// <remarks>
/// Http/DaprService bindings are classes with init-only members, so the copy is spelled out member by
/// member; <c>TaskBindingCredentialsTests</c> fails if a member is added to either binding without being
/// copied here.
/// </remarks>
public static class TaskBindingCredentials
{
    /// <summary>The HTTP binding with the caller credential merged into its headers.</summary>
    public static HttpTaskBinding WithCallerCredential(
        HttpTaskBinding binding, IReadOnlyDictionary<string, string>? callerHeaders)
    {
        var merged = HttpTaskInvocation.WithCallerCredential(binding.Headers, callerHeaders);
        if (merged == binding.Headers)
            return binding;

        return new HttpTaskBinding
        {
            Url = binding.Url,
            Method = binding.Method,
            Headers = merged,
            Body = binding.Body,
            ContentType = binding.ContentType,
            TimeoutSeconds = binding.TimeoutSeconds,
            ValidateSSL = binding.ValidateSSL,
            AcceptedStatusCodes = binding.AcceptedStatusCodes
        };
    }

    /// <summary>The SOAP binding with the caller credential merged into its headers.</summary>
    public static SoapTaskBinding WithCallerCredential(
        SoapTaskBinding binding, IReadOnlyDictionary<string, string>? callerHeaders)
    {
        var merged = HttpTaskInvocation.WithCallerCredential(binding.Headers, callerHeaders);
        return merged == binding.Headers ? binding : binding with { Headers = merged };
    }

    /// <summary>The Dapr service-invocation binding with the caller credential merged into its headers.</summary>
    public static DaprServiceBinding WithCallerCredential(
        DaprServiceBinding binding, IReadOnlyDictionary<string, string>? callerHeaders)
    {
        var merged = HttpTaskInvocation.WithCallerCredential(binding.Headers, callerHeaders);
        if (merged == binding.Headers)
            return binding;

        return new DaprServiceBinding
        {
            AppId = binding.AppId,
            MethodName = binding.MethodName,
            Method = binding.Method,
            QueryString = binding.QueryString,
            Headers = merged,
            Body = binding.Body,
            AcceptedStatusCodes = binding.AcceptedStatusCodes
        };
    }
}
