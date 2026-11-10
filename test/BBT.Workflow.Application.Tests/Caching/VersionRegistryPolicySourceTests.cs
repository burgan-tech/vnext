using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Unit tests for <see cref="VersionRegistryPolicySource"/>: the registry-backed read of a flow's
/// default-version pointer from the <c>sys-version-registry</c> system flow.
/// </summary>
public class VersionRegistryPolicySourceTests
{
    private const string Domain = "core";
    private const string FlowKey = "account-opening";

    private readonly IInstanceRepository _repository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _cacheStore = Substitute.For<IComponentCacheStore>();
    private readonly ICurrentSchema _currentSchema = Substitute.For<ICurrentSchema>();
    private readonly IRuntimeInfoProvider _runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
    private readonly ILogger<VersionRegistryPolicySource> _logger = Substitute.For<ILogger<VersionRegistryPolicySource>>();
    private readonly VersionRegistryPolicySource _sut;

    public VersionRegistryPolicySourceTests()
    {
        var uow = Substitute.For<IUnitOfWork>();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);

        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _runtimeInfo.UseDomain(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());
        _currentSchema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());

        var services = new ServiceCollection();
        services.AddSingleton(uowManager);
        services.AddSingleton(_repository);
        services.AddSingleton(_cacheStore);
        services.AddSingleton(_currentSchema);
        services.AddSingleton(_runtimeInfo);
        var provider = services.BuildServiceProvider();

        _sut = new VersionRegistryPolicySource(provider.GetRequiredService<IServiceScopeFactory>(), _logger);

        RegistryFlowPublished();
    }

    private void RegistryFlowPublished()
        => _cacheStore.GetFlowAsync(Domain, RuntimeSysSchemaInfo.VersionRegistry, null, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(null!));

    private static InstanceData Row(string dataVersion, string json, bool isLatest = true)
    {
        var ctor = typeof(InstanceData).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Guid), typeof(Guid), typeof(string), typeof(JsonData), typeof(bool)])!;
        return (InstanceData)ctor.Invoke([Guid.NewGuid(), Guid.NewGuid(), dataVersion, new JsonData(json), isLatest]);
    }

    private void Stored(string json)
        => _repository.FindLatestDataByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).Returns(Row("1.0.0", json));

    [Fact]
    public async Task ReadPointer_ShouldReturnNull_WhenRegistryFlowNotPublished()
    {
        _cacheStore.GetFlowAsync(Domain, RuntimeSysSchemaInfo.VersionRegistry, null, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Fail(
                CacheErrors.ItemNotFoundInBackend<Definitions.Workflow>(Domain, RuntimeSysSchemaInfo.VersionRegistry, null)));

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
        await _repository.DidNotReceiveWithAnyArgs().FindLatestDataByKeyAsync(default!, default);
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnNull_WhenNoInstanceForKey()
    {
        _repository.FindLatestDataByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).Returns((InstanceData?)null);

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnLatestDataPointer()
    {
        Stored("""{"type":"Flow","defaultVersion":"2.1"}""");

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("2.1");
    }

    /// <summary>
    /// A registry transition commits with the instance Busy, and the post-commit invalidation bumps
    /// the flow's generation before the runner settles it Active. A status-filtered read in that gap
    /// would see no row and cache the newest version under the NEW generation; the read must be the
    /// status-independent one.
    /// </summary>
    [Fact]
    public async Task ReadPointer_ShouldReturnPointer_WhenRegistryInstanceIsBusy()
    {
        Stored("""{"type":"Flow","defaultVersion":"2.1"}""");
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceAndDataModel>());

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.Value.ShouldBe("2.1");
        await _repository.DidNotReceiveWithAnyArgs().GetActiveDataListByKeyAsync(default!, default);
    }

    /// <summary>
    /// A Faulted (or stranded Busy) registry instance still carries the pointer its last committed
    /// row set; ignoring it would silently fall back to the newest version (fail-open).
    /// </summary>
    [Fact]
    public async Task ReadPointer_ShouldReturnPointer_WhenRegistryInstanceIsFaulted()
    {
        Stored("""{"type":"Flow","defaultVersion":"2"}""");
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceAndDataModel>());

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("2");
    }

    [Fact]
    public async Task ReadPointer_ShouldIgnoreType()
    {
        Stored("""{"type":"Function","defaultVersion":"3"}""");

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("3");
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnNull_WhenPointerIsNull()
    {
        Stored("""{"type":"Flow","defaultVersion":null}""");

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ReadPointer_ShouldReadUnderTheRegistrySchema()
    {
        _repository.FindLatestDataByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).Returns((InstanceData?)null);

        await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        _currentSchema.Received(1).Change(RuntimeSysSchemaInfo.VersionRegistry);
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnFail_WhenRepositoryThrows()
    {
        _repository.FindLatestDataByKeyAsync(FlowKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.VersionRegistryUnavailable);
        result.Error.Code.ShouldBe("Instance:100053");
        _logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log) &&
            c.GetArguments()[2]!.ToString()!.Contains("Version registry read failed"));
    }

    /// <summary>
    /// The 503 reaches the client: it carries fixed text only, the exception (with its message) goes
    /// to the log alone.
    /// </summary>
    [Fact]
    public async Task ReadPointer_ShouldLogExceptionButNotExposeIt_WhenRepositoryThrows()
    {
        var boom = new InvalidOperationException("db down");
        _repository.FindLatestDataByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).ThrowsAsync(boom);

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.Error.Message.ShouldNotContain("db down");
        result.Error.Message.ShouldContain("retry");
        _logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log) && ReferenceEquals(c.GetArguments()[3], boom));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("LATEST")]
    public async Task ReadPointer_ShouldReturnNull_WhenPointerIsLatest(string pointer)
    {
        Stored($$"""{"type":"Flow","defaultVersion":"{{pointer}}"}""");

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnFail_WhenStoredPointerIsInvalid()
    {
        Stored("""{"type":"Flow","defaultVersion":"2.x"}""");

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
    }

    /// <summary>
    /// A stored non-string pointer is a broken pointer (409), not an outage (503).
    /// </summary>
    [Fact]
    public async Task ReadPointer_ShouldReturnPointerInvalid_WhenDefaultVersionIsNotAString()
    {
        Stored("""{"type":"Flow","defaultVersion":2}""");

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
        _logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log) &&
            c.GetArguments()[2]!.ToString()!.Contains("Flow default version is invalid"));
        _logger.ReceivedCalls().ShouldNotContain(c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log) &&
            c.GetArguments()[2]!.ToString()!.Contains("Version registry read failed"));
    }
}
