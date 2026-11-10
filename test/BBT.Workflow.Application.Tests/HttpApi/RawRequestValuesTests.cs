using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Shouldly;
using Xunit;

namespace BBT.Workflow.HttpApi;

public class RawRequestValuesTests
{
    [Fact]
    public void RawHeaders_TwoStringValues_AreReportedAsTwoValues()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["x-branch-code"] = new StringValues(["BR-001", "BR-002"]);

        ctx.GetRawRequestValues().Headers["X-Branch-Code"].ShouldBe(["BR-001", "BR-002"]);
    }

    [Fact]
    public void RawQuery_RepeatedKey_KeepsBoth()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?branch=BR-001&branch=BR-002");

        ctx.GetRawRequestValues().Query["branch"].ShouldBe(["BR-001", "BR-002"]);
    }

    [Fact]
    public void GetRequestBindingContext_StillKeepsFirstValueOnly()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["x-branch-code"] = new StringValues(["BR-001", "BR-002"]);

        ctx.GetRequestBindingContext().Headers["x-branch-code"].ShouldBe("BR-001");
    }
}
