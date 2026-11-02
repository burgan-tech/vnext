namespace BBT.Workflow.SubFlow.Headers;

/// <summary>Receiver-side helpers shared by the SubFlow terminal services.</summary>
public static class CarriedHeaderPolicyExtensions
{
    /// <summary>
    /// Headers for the parent resume context: the re-filtered carried set when the policy's
    /// <c>ApplyToResume</c> is on, otherwise (or when no policy is wired) an empty set. Never null.
    /// </summary>
    public static Dictionary<string, string?> ResolveResumeHeaders(
        this ICarriedHeaderPolicy? policy,
        IReadOnlyDictionary<string, string?>? carried)
        => policy is { ApplyToResume: true }
            ? policy.Apply(carried)
            : new Dictionary<string, string?>();
}
