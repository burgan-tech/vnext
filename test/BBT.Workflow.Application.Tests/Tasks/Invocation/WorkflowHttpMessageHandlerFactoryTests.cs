using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Core.Http;
using BBT.Workflow.Tasks.Invocation;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The one handler build both hosts share. Pins the pool shape (a pooled <c>SocketsHttpHandler</c>,
/// never an <c>HttpClientHandler</c> rotated by the factory) and that the two hosts' option classes
/// project the same defaults, so a task sees one connection policy whichever host calls out.
/// </summary>
public sealed class WorkflowHttpMessageHandlerFactoryTests
{
    [Fact]
    public void Create_ShouldShapeThePoolFromSettings()
    {
        var settings = new WorkflowHttpClientPoolSettings(
            MaxConnectionsPerServer: 7,
            PooledConnectionLifetime: TimeSpan.FromMinutes(3),
            PooledConnectionIdleTimeout: TimeSpan.FromSeconds(45));

        using var handler = WorkflowHttpMessageHandlerFactory.Create(settings, validateSsl: true);

        handler.MaxConnectionsPerServer.ShouldBe(7);
        handler.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(3));
        handler.PooledConnectionIdleTimeout.ShouldBe(TimeSpan.FromSeconds(45));
        handler.AutomaticDecompression.ShouldBe(DecompressionMethods.GZip | DecompressionMethods.Deflate);
        handler.UseCookies.ShouldBeFalse();
        handler.SslOptions.RemoteCertificateValidationCallback.ShouldBeNull();
    }

    [Fact]
    public void Create_WithoutSslValidation_ShouldAcceptAnyServerCertificate()
    {
        using var handler = WorkflowHttpMessageHandlerFactory.Create(
            new WorkflowHttpClientPoolSettings(1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            validateSsl: false);

        var callback = handler.SslOptions.RemoteCertificateValidationCallback;
        callback.ShouldNotBeNull();
        callback!(this, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            .ShouldBeTrue();
    }

    [Fact]
    public void HandlerLifetime_ShouldBeInfinite()
    {
        // A finite lifetime re-introduces the pool-wide drop the SocketsHttpHandler build removes.
        WorkflowHttpMessageHandlerFactory.HandlerLifetime.ShouldBe(System.Threading.Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void BothHosts_ShouldProjectTheSameDefaultPool()
    {
        var orchestration = new TaskInvocationOptions().ToHttpClientPoolSettings();
        var execution = new WorkflowHttpClientOptions().ToPoolSettings();

        execution.ShouldBe(orchestration);
        orchestration.MaxConnectionsPerServer.ShouldBe(50);
        orchestration.PooledConnectionLifetime.ShouldBe(TimeSpan.FromSeconds(600));
        orchestration.PooledConnectionIdleTimeout.ShouldBe(TimeSpan.FromSeconds(120));
    }
}
