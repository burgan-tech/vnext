using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Runtime;

/// <summary>
/// Pins what <see cref="RuntimeService"/> may and may not skip while turning stored rows into components:
/// a malformed row is skipped, but a row the process failed to load for any other reason — out of memory
/// above all — must fail the read, because a silently shortened version list resolves to an older version.
/// </summary>
public sealed class RuntimeServiceTests
{
    private const string Key = "contract-approval-workflow";

    private readonly IInstanceRepository _repository = Substitute.For<IInstanceRepository>();
    private readonly RuntimeService _sut;

    public RuntimeServiceTests()
    {
        var options = new RuntimeOptions();
        options.Schemas.Add(ProbeEntity.ComponentTypeKey, "sys_probe", typeof(ProbeEntity));

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.Domain.Returns("core");

        _sut = new RuntimeService(
            _repository,
            Substitute.For<ICurrentSchema>(),
            Options.Create(options),
            runtimeInfo,
            NullLogger<RuntimeService>.Instance);
    }

    [Fact]
    public async Task GetVersionsAsync_ReadsVersionStringsOnly()
    {
        _repository.GetActiveVersionsByKeyAsync(Key, Arg.Any<CancellationToken>())
            .Returns(["1.0.0-pkg.1.0.9+core", "1.0.0-pkg.1.0.123+core"]);

        var versions = await _sut.GetVersionsAsync<ProbeEntity>(Key);

        versions.ShouldBe(["1.0.0-pkg.1.0.9+core", "1.0.0-pkg.1.0.123+core"]);
        await _repository.DidNotReceiveWithAnyArgs().GetActiveDataListByKeyAsync(default!, default);
    }

    [Fact]
    public async Task GetAsyncByKey_WhenARowRunsOutOfMemory_FailsInsteadOfReturningTheRest()
    {
        _repository.GetActiveDataListByKeyAsync(Key, Arg.Any<CancellationToken>())
            .Returns([Row("1.0.0-pkg.1.0.9+core", """{"name":"old"}"""), Row("1.0.0-pkg.1.0.123+core", """{"explode":true}""")]);

        await Should.ThrowAsync<OutOfMemoryException>(() => _sut.GetAsync<ProbeEntity>(Key));
    }

    [Fact]
    public async Task GetAsyncByKey_SkipsOnlyAMalformedRow()
    {
        _repository.GetActiveDataListByKeyAsync(Key, Arg.Any<CancellationToken>())
            .Returns([Row("1.0.0-pkg.1.0.9+core", """{"name":"ok"}"""), Row("1.0.0-pkg.1.0.123+core", """{"name":42}""")]);

        var loaded = (await _sut.GetAsync<ProbeEntity>(Key)).ToList();

        loaded.Select(e => e!.Version).ShouldBe(["1.0.0-pkg.1.0.9+core"]);
    }

    [Fact]
    public async Task GetAsyncByKeyAndVersion_WhenTheRowRunsOutOfMemory_FailsInsteadOfReportingNotFound()
    {
        _repository.FindActiveDataAsync(Key, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Row("1.0.0-pkg.1.0.123+core", """{"explode":true}"""));

        await Should.ThrowAsync<OutOfMemoryException>(() => _sut.GetAsync<ProbeEntity>(Key, "1.0.0"));
    }

    [Fact]
    public async Task GetAsyncByKeyAndVersion_ReturnsNullForAMalformedRow()
    {
        _repository.FindActiveDataAsync(Key, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Row("1.0.0-pkg.1.0.123+core", """{"name":42}"""));

        (await _sut.GetAsync<ProbeEntity>(Key, "1.0.0")).ShouldBeNull();
    }

    private static InstanceAndDataModel Row(string version, string json)
    {
        var instance = Instance.Create(Guid.NewGuid(), ProbeEntity.ComponentTypeKey, "1.0.0", Key);
        return new InstanceAndDataModel
        {
            Instance = instance,
            InstanceData = new InstanceData(Guid.NewGuid(), instance.Id, version, new JsonData(json), isLatest: true)
        };
    }

    /// <summary>A component whose <c>explode</c> member throws the way a load that ran out of memory does.</summary>
    public sealed class ProbeEntity : IDomainEntity, IReferenceSetter
    {
        public static string ComponentTypeKey => "sys-probes";

        public string ComponentKey => ComponentTypeKey;

        public string Key { get; private set; } = string.Empty;

        public string Version { get; private set; } = string.Empty;

        public string Domain { get; private set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("explode")]
        public bool Explode
        {
            get => false;
            set
            {
                if (value)
                    throw new OutOfMemoryException("Simulated: the row did not fit in memory.");
            }
        }

        public void SetReference(IReference reference)
        {
            Key = reference.Key;
            Domain = reference.Domain;
            Version = reference.Version;
        }
    }
}
