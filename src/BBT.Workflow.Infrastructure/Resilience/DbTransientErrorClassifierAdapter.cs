using BBT.Workflow.Resilience;

namespace BBT.Workflow.Infrastructure.Resilience;

/// <summary>
/// Adapter that exposes <see cref="DbTransientErrorClassifier"/> as the
/// <see cref="IDbTransientErrorClassifier"/> interface, bridging the Infrastructure
/// static classifier with the Application/Domain abstraction layer.
/// </summary>
internal sealed class DbTransientErrorClassifierAdapter : IDbTransientErrorClassifier
{
    /// <inheritdoc />
    public bool IsRetriableTransient(Exception ex)
        => DbTransientErrorClassifier.IsRetriableTransient(ex);
}
