using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Uow;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Caching;

public sealed class CacheKeyInterpolatorTests : IDisposable
{
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public CacheKeyInterpolatorTests()
    {
        // Required for the PostSharp SchemaValidation aspect used by Instance.AddData.
        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton(Substitute.For<BBT.Workflow.Caching.IComponentCacheStore>());
        services.AddSingleton(Substitute.For<BBT.Workflow.DefinitionContext.IWorkflowContext>());
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
    }

    private static ScriptContext BuildContext(
        object? headers = null,
        string? instanceDataJson = null)
    {
        var builder = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>());

        if (headers is not null)
        {
            builder.SetHeaders(headers);
        }

        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        if (instanceDataJson is not null)
        {
            instance.AddData(Guid.NewGuid(), new JsonData(instanceDataJson));
        }

        builder.SetInstance(instance);
        return builder.Build();
    }

    [Fact]
    public void Interpolate_WithoutPlaceholders_ReturnsTemplateUnchanged()
    {
        var context = BuildContext();
        CacheKeyInterpolator.Interpolate("customer:profile", context).ShouldBe("customer:profile");
    }

    [Fact]
    public void Interpolate_ResolvesHeaderPlaceholder()
    {
        var context = BuildContext(headers: new Dictionary<string, string> { ["customerId"] = "42" });

        var result = CacheKeyInterpolator.Interpolate("customer:{context.Headers.customerId}:profile", context);

        result.ShouldBe("customer:42:profile");
    }

    [Fact]
    public void Interpolate_ResolvesInstanceDataPlaceholder()
    {
        var context = BuildContext(instanceDataJson: """{ "orderId": "abc-123" }""");

        var result = CacheKeyInterpolator.Interpolate("order:{context.Instance.Data.orderId}", context);

        result.ShouldBe("order:abc-123");
    }

    [Fact]
    public void Interpolate_IsCaseInsensitiveOnPath()
    {
        var context = BuildContext(headers: new Dictionary<string, string> { ["customerId"] = "7" });

        // Header keys are lowercased in the context; path matching is case-insensitive.
        CacheKeyInterpolator.Interpolate("{context.headers.CUSTOMERID}", context).ShouldBe("7");
    }

    [Fact]
    public void Interpolate_UnresolvedPlaceholder_Throws()
    {
        var context = BuildContext(headers: new Dictionary<string, string> { ["other"] = "x" });

        Should.Throw<InvalidOperationException>(() =>
            CacheKeyInterpolator.Interpolate("customer:{context.Headers.customerId}", context));
    }
}
