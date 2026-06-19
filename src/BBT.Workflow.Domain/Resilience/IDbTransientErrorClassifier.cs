namespace BBT.Workflow.Resilience;

/// <summary>
/// Classifies exceptions to determine whether they represent a genuinely retriable
/// transient database connection fault.
/// Pool-exhaustion ("pool has been exhausted") and server-side saturation (SqlState
/// 53300/53400) are intentionally excluded and must never be retried.
/// </summary>
public interface IDbTransientErrorClassifier
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> represents a retriable transient
    /// database fault; <c>false</c> for non-transient errors and pool-exhaustion/saturation.
    /// </summary>
    bool IsRetriableTransient(Exception ex);
}
