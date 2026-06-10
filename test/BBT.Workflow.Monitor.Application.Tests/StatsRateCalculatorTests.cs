using BBT.Workflow.Monitor.Stats;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests;

public sealed class StatsRateCalculatorTests
{
    [Fact]
    public void Rate_ZeroTotal_ReturnsZero() => Assert.Equal(0d, StatsRateCalculator.Rate(5, 0));

    [Fact]
    public void Rate_ComputesFraction() => Assert.Equal(0.25d, StatsRateCalculator.Rate(1, 4));
}
