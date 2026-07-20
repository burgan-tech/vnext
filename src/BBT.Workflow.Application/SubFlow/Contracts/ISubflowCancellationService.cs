namespace BBT.Workflow.SubFlow;

/// <summary>
/// Propagates a canceled SubItem outcome to its parent workflow.
/// </summary>
public interface ISubflowCancellationService
{
    Task CancellationAsync(
        SubItemCanceledInput input,
        CancellationToken cancellationToken = default);
}
