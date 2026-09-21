using System;
using System.Collections.Generic;
using BBT.Aether.Guids;
using BBT.Aether.Tracing;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Tasks.Invocation;
using BBT.Workflow.Tasks.Notification;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// Regression coverage for the container-build-time circular dependency (issue #1007 follow-up)
/// that kept the Orchestration host from starting at all:
/// <c>HttpTaskExecutor → ITaskInvocationDispatcher → ITaskInvocationRouter →
/// ILocalTaskInvokerRegistry → IEnumerable&lt;ILocalTaskInvoker&gt; → LocalCacheAsideTaskInvoker
/// → ILocalTaskInvokerRegistry</c>. No existing unit test caught this because every unit test
/// constructs these types by hand and no test built the real container with
/// <c>ValidateOnBuild</c>.
/// </summary>
/// <remarks>
/// Coverage scope: this test builds exactly the registrations
/// <c>TaskServiceCollectionExtensions.AddTaskHandlers()</c> contributes — the whole task-executor /
/// task-invocation / evaluator / error-boundary / coordination / factory / persistence / scripting
/// slice of the Application layer, which is where every participant in the cycle above lives — plus
/// the bare minimum of cross-cutting scaffolding those registrations themselves reach for but do not
/// register: <see cref="IConfiguration"/>, logging, <see cref="IHttpClientFactory"/> (a real
/// <c>AddHttpClient()</c> — this is what the local HTTP/SOAP invokers consume), and a handful of
/// leaf dependencies from OTHER modules stood in with NSubstitute fakes exactly as this repo's own
/// unit tests do (<see cref="DaprClient"/>, <see cref="ICorrelationIdProvider"/>,
/// <see cref="IComponentCacheStore"/>, <see cref="IInstanceTaskRepository"/>,
/// <see cref="IInstanceRepository"/>, <see cref="IUnitOfWorkManager"/>,
/// <see cref="INotificationChannelResolver"/>, <see cref="IRuntimeInfoProvider"/>) purely so the
/// graph is fully resolvable — none of them participates in the cycle this test guards against.
/// <para/>
/// It does NOT build the Infrastructure module, the rest of <c>AddApplicationModule</c>
/// (pipeline/app services/domain events/subflow), or anything Orchestration's own
/// <c>AddOrchestrationApiModule</c> adds (real Dapr client wiring, EF Core DbContext, distributed
/// cache/lock, telemetry, hosted services). A cycle introduced between a task-invocation type and
/// one of THOSE registrations would not be caught here — only a full
/// <c>AddOrchestrationApiModule()</c> build (which needs a live-shaped configuration for Postgres,
/// Redis and the Dapr sidecar) would close that gap.
/// </remarks>
public sealed class TaskInvocationDiRegistrationTests
{
    [Fact]
    public void AddTaskHandlers_Graph_Builds_Without_A_Circular_Dependency()
    {
        var services = BuildServices();

        var act = () => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        act.ShouldNotThrow();
    }

    /// <summary>
    /// Narrower probe naming the exact chain from the failure log, so a future regression that
    /// reintroduces the cycle through a different path still fails loudly here rather than only at
    /// real host startup.
    /// </summary>
    [Fact]
    public void LocalTaskInvokerRegistry_Resolves_Without_Constructing_Its_Own_Dependents_Eagerly()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<ILocalTaskInvokerRegistry>();
        registry.Has(TaskTypes.CacheAside).ShouldBeTrue();

        var dispatcher = scope.ServiceProvider.GetRequiredService<ITaskInvocationDispatcher>();
        dispatcher.ShouldNotBeNull();
    }

    private static ServiceCollection BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Read by GrpcTaskInvokerClientProvider's constructor (VNextAppIds.ExecutionOrDefault)
                // only when something actually RESOLVES it (it is lazy beyond that — see its own
                // remarks); ValidateOnBuild itself never invokes a constructor, only the static graph.
                // Present so a test that does resolve the full dispatcher graph does not fail on
                // unrelated missing configuration.
                ["APP_DOMAIN"] = "test-domain"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddHttpClient();

        // Leaf dependencies owned by other modules (Aether, Infrastructure) that AddTaskHandlers()
        // itself does not register but some of its services reach for. Stood in with fakes so the
        // graph is fully resolvable — none of these participates in the cycle this test guards
        // against (see the class remarks for exact coverage scope).
        services.AddSingleton(Substitute.For<DaprClient>());
        services.AddSingleton(Substitute.For<ICorrelationIdProvider>());
        services.AddSingleton(Substitute.For<IComponentCacheStore>());
        services.AddScoped(_ => Substitute.For<IInstanceTaskRepository>());
        services.AddScoped(_ => Substitute.For<IInstanceRepository>());
        services.AddScoped(_ => Substitute.For<IInstanceTransitionRepository>());
        services.AddScoped(_ => Substitute.For<IInstanceDataWriteService>());
        services.AddSingleton(Substitute.For<IUnitOfWorkManager>());
        services.AddSingleton(Substitute.For<INotificationChannelResolver>());
        services.AddSingleton(Substitute.For<IRuntimeInfoProvider>());
        services.AddScoped(_ => Substitute.For<IInstanceCommandGateway>());
        services.AddScoped(_ => Substitute.For<IInstanceQueryGateway>());
        services.AddSingleton(Substitute.For<IGuidGenerator>());
        services.AddScoped(_ => Substitute.For<IDomainDiscoveryResolver>());

        // A real (not faked) registration: self-contained (options + logger only), and it is the
        // Application module's own service (WorkflowApplicationModuleServiceCollectionExtensions),
        // not a different module's leaf dependency.
        services.AddResultResilience();

        services.AddTaskHandlers();

        return services;
    }
}
