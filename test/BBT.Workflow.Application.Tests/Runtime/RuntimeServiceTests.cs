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
/// Pins what <see cref="RuntimeService"/> may and may not skip while turning a stored row into a component —
/// a malformed row is skipped, but a row the process failed to load for any other reason, out of memory above
/// all, must fail the read — and that its version reads stay on the version column of the component's schema.
/// </summary>
public sealed class RuntimeServiceTests
{
    private const string Key = "contract-approval-workflow";

    private readonly IInstanceRepository _repository = Substitute.For<IInstanceRepository>();
    private readonly ICurrentSchema _currentSchema = Substitute.For<ICurrentSchema>();
    private readonly RuntimeService _sut;

    public RuntimeServiceTests()
    {
        var options = new RuntimeOptions();
        options.Schemas.Add(ProbeEntity.ComponentTypeKey, "sys_probe", typeof(ProbeEntity));

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.Domain.Returns("core");

        _sut = new RuntimeService(
            _repository,
            _currentSchema,
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
        _currentSchema.Received(1).Change("sys_probe");
    }

    [Fact]
    public async Task GetVersionPageAsync_PagesTheVersionMetadataOfTheComponentsSchema()
    {
        var publishedAt = new DateTime(2026, 11, 9, 9, 28, 0, DateTimeKind.Utc);
        _repository.GetVersionsPagedAsync(Key, 20, 21, Arg.Any<CancellationToken>())
            .Returns([new ComponentVersionSummary("1.0.0-pkg.1.0.350+contract", true, "1.0.0", publishedAt)]);

        var page = await _sut.GetVersionPageAsync<ProbeEntity>(Key, skip: 20, take: 21);

        page.ShouldHaveSingleItem().Version.ShouldBe("1.0.0-pkg.1.0.350+contract");
        _currentSchema.Received(1).Change("sys_probe");
    }

    [Fact]
    public async Task GetPublishedVersionsAsync_ListsEveryComponentsVersionsFromTheComponentsSchema()
    {
        var publishedAt = new DateTime(2026, 11, 9, 9, 28, 0, DateTimeKind.Utc);
        _repository.GetActiveComponentVersionsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new PublishedComponentVersion("flow-a", "1.0.0-pkg.1.0.2+core", publishedAt),
                new PublishedComponentVersion("flow-b", "2.0.0-pkg.1.0.1+core", publishedAt)
            ]);

        var versions = await _sut.GetPublishedVersionsAsync<ProbeEntity>();

        versions.Select(v => (v.Key, v.Version)).ShouldBe([("flow-a", "1.0.0-pkg.1.0.2+core"), ("flow-b", "2.0.0-pkg.1.0.1+core")]);
        _currentSchema.Received(1).Change("sys_probe");
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
