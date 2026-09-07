using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Discovery;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.Remote.Extensions;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Pins the retry split that <see cref="RemoteServiceProfile"/> introduces: read clients retry a
/// transient failure, mutating clients attempt exactly once.
/// </summary>
/// <remarks>
/// Behavioural rather than structural — it counts real attempts through the assembled
/// <c>IHttpClientFactory</c> pipeline instead of inspecting handler lists, so it keeps holding if
/// the pipeline is reordered.
/// <para>
/// This is the assertion that protects against duplicated side effects: a retried
/// <c>instances/start</c> or <c>internal/subflow-forward</c> is data corruption, not a slow call.
/// </para>
/// </remarks>
public sealed class RemoteServiceProfileTests
{
    private const int MaxRetryAttempts = 2;

    #region Read Profile

    [Fact]
    public async Task Read_Profile_Should_Retry_Transient_Failures()
    {
        var (client, counter) = BuildProbe(RemoteServiceProfile.Read);

        await client.CallAsync();

        // 1 initial attempt + MaxRetryAttempts retries.
        counter.Attempts.ShouldBe(1 + MaxRetryAttempts);
    }

    #endregion

    #region Mutating Profile

    /// <summary>
    /// The core of the decision: no transport-level retry on side-effecting endpoints. The
    /// failure surfaces as <c>Error.Transient("remote_network_error", …)</c> and the
    /// user-defined error boundary — the only layer that knows whether repeating is safe —
    /// decides what happens next.
    /// </summary>
    [Fact]
    public async Task Mutating_Profile_Should_Attempt_Exactly_Once()
    {
        var (client, counter) = BuildProbe(RemoteServiceProfile.Mutating);

        await client.CallAsync();

        counter.Attempts.ShouldBe(1);
    }

    /// <summary>
    /// The emergency reversal, off by default. It exists because the split is a CODE change and
    /// therefore outside the <c>ServiceDiscovery:Provider</c> switch's scope.
    /// </summary>
    [Fact]
    public async Task Mutating_Profile_Should_Retry_When_Explicitly_Re_Enabled()
    {
        var (client, counter) = BuildProbe(
            RemoteServiceProfile.Mutating, enableRetryOnMutating: true);

        await client.CallAsync();

        counter.Attempts.ShouldBe(1 + MaxRetryAttempts);
    }

    #endregion

    #region Default

    /// <summary>
    /// The parameter defaults to <c>Read</c>, so an <c>AddRemoteService</c> call that forgets to
    /// state its profile keeps the pre-split behaviour rather than silently dropping retry.
    /// </summary>
    [Fact]
    public async Task Omitted_Profile_Should_Default_To_Read()
    {
        var (client, counter) = BuildProbe(profile: null);

        await client.CallAsync();

        counter.Attempts.ShouldBe(1 + MaxRetryAttempts);
    }

    #endregion

    private static (ProbeClient Client, AttemptCounter Counter) BuildProbe(
        RemoteServiceProfile? profile,
        bool enableRetryOnMutating = false)
    {
        var options = new RemoteOptions
        {
            BaseUrl = "https://unused.test",
            TimeoutSeconds = 30,
            MaxRetryAttempts = MaxRetryAttempts,
            RetryDelayMilliseconds = 1,
            EnableRetryOnMutating = enableRetryOnMutating
        };

        var counter = new AttemptCounter();
        var services = new ServiceCollection();

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.Domain.Returns("credit");
        runtimeInfo.Version.Returns("test");
        services.AddSingleton(runtimeInfo);
        services.Configure<RemoteOptions>(o =>
        {
            o.TimeoutSeconds = options.TimeoutSeconds;
            o.EnableCircuitBreakerBypass = false;
        });

        var builder = profile is null
            ? services.AddRemoteService<ProbeClient, ProbeClient>(options)
            : services.AddRemoteService<ProbeClient, ProbeClient>(options, profile.Value);

        // Replaces the primary handler configured inside AddRemoteService (last registration
        // wins) so attempts are counted without leaving the process.
        builder.ConfigurePrimaryHttpMessageHandler(() => counter);

        var client = services.BuildServiceProvider().GetRequiredService<ProbeClient>();
        return (client, counter);
    }

    /// <summary>Minimal remote client — exercises the generic extension through the shell, nothing else.</summary>
    private sealed class ProbeClient(IRemoteTransport<ProbeClient> transport)
    {
        private static readonly DiscoveryEndpoint Endpoint =
            new(EndpointKind.Url, new Uri("https://remote.test/"));

        public async Task<HttpStatusCode> CallAsync()
        {
            var response = await transport.SendAsync(
                Endpoint, HttpMethod.Get, "api/v1.0/probe", configure: null, CancellationToken.None);
            return response.StatusCode;
        }
    }

    /// <summary>Always answers 503, which Polly's transient-error predicate retries.</summary>
    private sealed class AttemptCounter : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
