using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Exception thrown when a Subflow completion fails.
/// </summary>
/// <param name="domain">The domain of the Subflow.</param>
/// <param name="flow">The flow of the Subflow.</param>
/// <param name="instance">The instance of the Subflow.</param>
/// <param name="errorCode">The error code of the Subflow completion failure.</param>
/// <param name="reason">The reason of the Subflow completion failure.</param>
public class SubflowCompletionException(string domain, string flow, string instance, string errorCode, string reason) : UserFriendlyException(
    code: WorkflowErrorCodes.SubflowCompletionFailed,
    message: $"Subflow completion failed for domain: {domain}, flow: {flow}, instance: {instance}, error code: {errorCode}, reason: {reason}");