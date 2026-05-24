using BBT.Workflow.Execution;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks;

public sealed class AcceptedStatusCodeMatcherTests
{
    [Theory]
    [InlineData(400, "400")]
    [InlineData(400, "4xx")]
    [InlineData(404, "40x")]
    [InlineData(500, "5XX")]
    public void IsAccepted_WithExactOrWildcardPattern_ReturnsTrue(int statusCode, string pattern)
    {
        AcceptedStatusCodeMatcher.IsAccepted(statusCode, [pattern]).ShouldBeTrue();
    }

    [Theory]
    [InlineData(400, "5xx")]
    [InlineData(404, "41x")]
    [InlineData(500, "400")]
    public void IsAccepted_WhenPatternDoesNotMatch_ReturnsFalse(int statusCode, string pattern)
    {
        AcceptedStatusCodeMatcher.IsAccepted(statusCode, [pattern]).ShouldBeFalse();
    }
}
