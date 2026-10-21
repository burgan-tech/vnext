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

    private static InstanceAndDataModel Row(string dataVersion, string json, bool isLatest)
    {
        var ctor = typeof(InstanceData).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Guid), typeof(Guid), typeof(string), typeof(JsonData), typeof(bool)])!;
        var data = (InstanceData)ctor.Invoke([Guid.NewGuid(), Guid.NewGuid(), dataVersion, new JsonData(json), isLatest]);
        return new InstanceAndDataModel { InstanceData = data };
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnNull_WhenRegistryFlowNotPublished()
    {
        _cacheStore.GetFlowAsync(Domain, RuntimeSysSchemaInfo.VersionRegistry, null, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Fail(
                CacheErrors.ItemNotFoundInBackend<Definitions.Workflow>(Domain, RuntimeSysSchemaInfo.VersionRegistry, null)));

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
        await _repository.DidNotReceiveWithAnyArgs().GetActiveDataListByKeyAsync(default!, default);
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnNull_WhenNoInstanceForKey()
    {
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceAndDataModel>());

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnLatestDataPointer()
    {
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).Returns(
        [
            Row("1.0.0", """{"type":"Flow","defaultVersion":"2.0"}""", false),
            Row("1.1.0", """{"type":"Flow","defaultVersion":"2.1"}""", true)
        ]);

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("2.1");
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnHighestDataVersion_WhenNoRowIsLatest()
    {
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).Returns(
        [
            Row("1.0.0", """{"defaultVersion":"2.0"}""", false),
            Row("1.10.0", """{"defaultVersion":"2.1"}""", false),
            Row("1.2.0", """{"defaultVersion":"2.2"}""", false)
        ]);

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.Value.ShouldBe("2.1");
    }

    [Fact]
    public async Task ReadPointer_ShouldIgnoreType()
    {
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).Returns(
        [
            Row("1.0.0", """{"type":"Function","defaultVersion":"3"}""", true)
        ]);

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("3");
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnNull_WhenPointerIsNull()
    {
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).Returns(
        [
            Row("1.0.0", """{"type":"Flow","defaultVersion":null}""", true)
        ]);

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ReadPointer_ShouldReadUnderTheRegistrySchema()
    {
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceAndDataModel>());

        await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        _currentSchema.Received(1).Change(RuntimeSysSchemaInfo.VersionRegistry);
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnFail_WhenRepositoryThrows()
    {
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.VersionRegistryUnavailable);
        result.Error.Code.ShouldBe("Instance:100053");
        _logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log) &&
            c.GetArguments()[2]!.ToString()!.Contains("Version registry read failed"));
    }

    [Fact]
    public async Task ReadPointer_ShouldLogException_WhenRepositoryThrows()
    {
        var boom = new InvalidOperationException("db down");
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).ThrowsAsync(boom);

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.Error.Message.ShouldContain("db down");
        _logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log) && ReferenceEquals(c.GetArguments()[3], boom));
    }

    [Fact]
    public async Task ReadPointer_ShouldReturnFail_WhenStoredPointerIsInvalid()
    {
        _repository.GetActiveDataListByKeyAsync(FlowKey, Arg.Any<CancellationToken>()).Returns(
        [
            Row("1.0.0", """{"type":"Flow","defaultVersion":"latest"}""", true)
        ]);

        var result = await _sut.GetDefaultVersionAsync(Domain, FlowKey, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
    }
}
