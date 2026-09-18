using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using BBT.Workflow.Instances.Caching;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.HumanTask;

/// <summary>
/// The human-task bounds are validated at startup, and these pin what that validation is for.
/// </summary>
/// <remarks>
/// Every bound on <see cref="HumanTaskFunctionOptions"/> is a loop stride, a semaphore size or a
/// degree of parallelism. None of them fails loudly on a bad value — the failure modes are a spin,
/// a deadlock and an unbounded fan-out, all on a public read path, and all of them look like "the
/// endpoint is slow" from outside. Binding alone would let a typo in a config map reach production;
/// <c>ValidateDataAnnotations().ValidateOnStart()</c> turns each one into a boot failure instead.
/// </remarks>
public class HumanTaskFunctionOptionsValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(object options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void TheDefaults_AreValid()
    {
        Validate(new HumanTaskFunctionOptions()).ShouldBeEmpty();
        Validate(new HumanTaskFunctionCacheOptions()).ShouldBeEmpty();
    }

    /// <summary>
    /// Zero is the value that hangs: the scan's batching loop advances the offset by
    /// <c>FlowsPerScanStatement</c>, so a zero leaves it where it was and spins forever while
    /// holding the scan's connection open. Nothing is logged, and no request ever answers.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_001)]
    public void FlowsPerScanStatement_OutsideItsRange_IsRejected(int value)
    {
        var results = Validate(new HumanTaskFunctionOptions { FlowsPerScanStatement = value });

        results.ShouldContain(r => r.MemberNames.Contains(nameof(HumanTaskFunctionOptions.FlowsPerScanStatement)));
    }

    /// <summary>
    /// <c>Parallel.ForEachAsync</c> reads a NEGATIVE <c>MaxDegreeOfParallelism</c> as unbounded, so
    /// a sign slip here is not a smaller fan-out — it is one branch, and one pooled connection, per
    /// candidate-bearing flow with no ceiling at all. That is the exact failure this PR exists to
    /// remove, reachable again through configuration.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    public void FanoutParallelism_OutsideItsRange_IsRejected(int value)
    {
        var results = Validate(new HumanTaskFunctionOptions { FanoutParallelism = value });

        results.ShouldContain(r => r.MemberNames.Contains(nameof(HumanTaskFunctionOptions.FanoutParallelism)));
    }

    /// <summary>A zero-capacity semaphore admits nobody: every descent would wait forever.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_025)]
    public void MaxConcurrentDescents_OutsideItsRange_IsRejected(int value)
    {
        var results = Validate(new HumanTaskFunctionOptions { MaxConcurrentDescents = value });

        results.ShouldContain(r => r.MemberNames.Contains(nameof(HumanTaskFunctionOptions.MaxConcurrentDescents)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5_001)]
    public void PerSchemaLimit_OutsideItsRange_IsRejected(int value) =>
        Validate(new HumanTaskFunctionOptions { PerSchemaLimit = value })
            .ShouldContain(r => r.MemberNames.Contains(nameof(HumanTaskFunctionOptions.PerSchemaLimit)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void ResultCap_OutsideItsRange_IsRejected(int value) =>
        Validate(new HumanTaskFunctionOptions { ResultCap = value })
            .ShouldContain(r => r.MemberNames.Contains(nameof(HumanTaskFunctionOptions.ResultCap)));

    /// <summary>A depth of zero would resolve no leaf at all and report every candidate unresolved.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void MaxDescentDepth_OutsideItsRange_IsRejected(int value) =>
        Validate(new HumanTaskFunctionOptions { MaxDescentDepth = value })
            .ShouldContain(r => r.MemberNames.Contains(nameof(HumanTaskFunctionOptions.MaxDescentDepth)));

    /// <summary>
    /// A non-positive TTL does not disable the cache — <c>Enabled</c> is the kill switch. It writes
    /// entries that are already expired, so every request pays a full fan-out AND two cache round
    /// trips: strictly worse than having no cache.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void CacheTtlSeconds_OutsideItsRange_IsRejected(int value) =>
        Validate(new HumanTaskFunctionCacheOptions { TtlSeconds = value })
            .ShouldContain(r => r.MemberNames.Contains(nameof(HumanTaskFunctionCacheOptions.TtlSeconds)));
}
