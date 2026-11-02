namespace BBT.Workflow.Execution;

/// <summary>
/// Result-metadata keys a SubProcess start reports, shared by the local executor and the remote
/// invoker so both paths feed the parent's correlation through the same key.
/// </summary>
public static class SubProcessMetadataKeys
{
    /// <summary>The child's resolved (full) flow version; absent when the remote runtime reports none.</summary>
    public const string ResolvedFlowVersion = "SubFlowVersion";
}
