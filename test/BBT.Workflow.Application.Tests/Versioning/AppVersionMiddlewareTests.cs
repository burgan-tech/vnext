using System;
using System.Threading.Tasks;
using BBT.Workflow.Middlewares;
using BBT.Workflow.Runtime;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Versioning;

/// <summary>
/// Unit tests for <see cref="AppVersionMiddleware"/>.
/// Verifies that X-App-Version header is added to every response.
/// </summary>
public class AppVersionMiddlewareTests
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;

    public AppVersionMiddlewareTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _runtimeInfoProvider.Version.Returns("3.2.1");
    }

    [Fact]
    public async Task InvokeAsync_ShouldAddXAppVersionHeader()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var middleware = new AppVersionMiddleware(_ => Task.CompletedTask, _runtimeInfoProvider);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.Headers["X-App-Version"].ToString().ShouldBe("3.2.1");
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNextMiddleware()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var nextCalled = false;
        var middleware = new AppVersionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, _runtimeInfoProvider);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_WhenNextThrows_ShouldStillHaveHeader()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var middleware = new AppVersionMiddleware(
            _ => throw new InvalidOperationException("boom"),
            _runtimeInfoProvider);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        context.Response.Headers["X-App-Version"].ToString().ShouldBe("3.2.1");
    }
}
