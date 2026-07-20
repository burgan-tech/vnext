using System.Net;
using BBT.Workflow.Shared;
using Xunit;

namespace BBT.Workflow.Shared;

public class TransientHttpStatusTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)] // 500 — regression guard: was dropped by the old "> 500" check
    [InlineData(HttpStatusCode.NotImplemented)] // 501
    [InlineData(HttpStatusCode.BadGateway)] // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)] // 503
    [InlineData(HttpStatusCode.GatewayTimeout)] // 504
    [InlineData(HttpStatusCode.RequestTimeout)] // 408
    [InlineData(HttpStatusCode.TooManyRequests)] // 429
    public void IsTransient_ShouldReturnTrue_ForRetryableStatuses(HttpStatusCode status)
    {
        Assert.True(TransientHttpStatus.IsTransient(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)] // 400
    [InlineData(HttpStatusCode.Unauthorized)] // 401
    [InlineData(HttpStatusCode.Forbidden)] // 403
    [InlineData(HttpStatusCode.NotFound)] // 404
    [InlineData(HttpStatusCode.Conflict)] // 409
    [InlineData(HttpStatusCode.UnprocessableEntity)] // 422
    public void IsTransient_ShouldReturnFalse_ForPermanentClientErrors(HttpStatusCode status)
    {
        Assert.False(TransientHttpStatus.IsTransient(status));
    }
}
