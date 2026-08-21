using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Tasks.Executors.FanOut;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public class FanOutConcurrencyLimiterTests
{
    [Fact]
    public async Task Limiter_Should_Cap_Concurrent_Holders_At_MaxConcurrentItems()
    {
        var limiter = new FanOutConcurrencyLimiter(
            Options.Create(new FanOutOptions { MaxConcurrentItems = 2 }));

        var running = 0;
        var peak = 0;
        var gate = new object();

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await limiter.WaitAsync(CancellationToken.None);
            try
            {
                lock (gate) { running++; peak = Math.Max(peak, running); }
                await Task.Delay(20);
            }
            finally
            {
                lock (gate) { running--; }
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks);
        peak.ShouldBeLessThanOrEqualTo(2);
        limiter.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Limiter_Should_Actually_Saturate_The_Cap_Not_Just_Stay_Under_It()
    {
        // Guards against a vacuous pass: with enough concurrent holders in flight, the peak
        // must reach exactly the configured cap, not merely stay below it by accident of timing.
        var limiter = new FanOutConcurrencyLimiter(
            Options.Create(new FanOutOptions { MaxConcurrentItems = 3 }));

        var running = 0;
        var peak = 0;
        var gate = new object();

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await limiter.WaitAsync(CancellationToken.None);
            try
            {
                lock (gate) { running++; peak = Math.Max(peak, running); }
                await Task.Delay(15);
            }
            finally
            {
                lock (gate) { running--; }
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks);
        peak.ShouldBe(3);
    }

    [Fact]
    public void FanOutOptions_Should_Fail_Validation_When_MaxConcurrentItems_Is_Not_Positive()
    {
        var options = new FanOutOptions { MaxConcurrentItems = 0 };
        var context = new ValidationContext(options);
        var results = new System.Collections.Generic.List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, context, results, validateAllProperties: true);

        isValid.ShouldBeFalse();
    }
}
