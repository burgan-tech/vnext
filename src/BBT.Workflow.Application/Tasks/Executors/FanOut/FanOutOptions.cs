using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Process-level fan-out settings. Bound from configuration section "Workflow:FanOut".
/// </summary>
public sealed class FanOutOptions
{
    /// <summary>
    /// Configuration section name for fan-out options.
    /// </summary>
    public const string SectionName = "Workflow:FanOut";

    /// <summary>
    /// Global bulkhead: maximum fan-out items executing concurrently across ALL batches
    /// in this process. Effective per-batch concurrency = min(task maxDegreeOfParallelism,
    /// available global slots).
    /// </summary>
    /// <remarks>
    /// Must be at least 1. A value of 0 (or negative) would construct the backing semaphore
    /// with zero capacity, which deadlocks every fan-out batch in the process on its first
    /// item — validated at startup via <c>ValidateOnStart()</c> in the DI registration so this
    /// surfaces as a boot failure, not a silent hang.
    /// </remarks>
    [Range(1, int.MaxValue, ErrorMessage = "MaxConcurrentItems must be at least 1")]
    public int MaxConcurrentItems { get; set; } = 64;
}
