using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Authorization.RowSecurity;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions.CastHandlers;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// The publish path answers "does this version already exist" from the stored version strings
/// (<see cref="IInstanceRepository.FindDataVersionAsync"/>) on an aggregate loaded without data rows
/// (<see cref="IInstanceRepository.FindByIdentifierForDataAppendAsync"/>), never by materializing every
/// published body (<see cref="IInstanceRepository.FindByIdentifierWithFullDataAsync"/>). The answers stay
/// what they were: an existing version is a 409 that writes nothing, a new one is appended through the
/// write funnel with the tags saved.
/// </summary>
public sealed class DefinitionAppServicePublishVersionTests
{
    private const string Key = "publish-version-flow";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceDataWriteService _dataWriter = Substitute.For<IInstanceDataWriteService>();
    private readonly IWorkflowCastHandler _castHandler = Substitute.For<IWorkflowCastHandler>();
    private readonly DefinitionAppService _sut;

    public DefinitionAppServicePublishVersionTests()
    {
        var guids = Substitute.For<IGuidGenerator>();
        guids.Create().Returns(_ => Guid.NewGuid());

        var services = new ServiceCollection();
        services.AddLogging();
        // ApplicationService.Logger / GuidGenerator resolve through ILazyServiceProvider.
        services.AddTransient<ILazyServiceProvider, LazyServiceProvider>();
        services.AddSingleton(guids);
        services.AddSingleton(Substitute.For<ICurrentSchema>());
        services.AddSingleton(Substitute.For<ISchemaMigrationOrchestrator>());
        // The seed-data path resolves its repository and writer from a child scope.
        services.AddSingleton(_instanceRepository);
        services.AddSingleton(_dataWriter);
        var provider = services.BuildServiceProvider();

        _castHandler.CanHandle(Arg.Any<string>()).Returns(true);

        _sut = new DefinitionAppService(
            Substitute.For<ICurrentSchema>(),
            Substitute.For<IRuntimeInfoProvider>(),
            _instanceRepository,
            _dataWriter,
            new ComponentValidatorProcessor(
                [new FlowComponentValidator(new WorkflowValidator())],
                Substitute.For<IScriptAssemblyCatalog>()),
            new WorkflowCastProcessor([_castHandler]),
            Substitute.For<IDomainCacheContext>(),
            new RowSecurityPublishCheck(Substitute.For<IComponentCacheStore>(), NullLogger<RowSecurityPublishCheck>.Instance),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider);
    }

    [Fact]
    public async Task Publishing_an_existing_version_answers_conflict_and_writes_nothing()
    {
        var existing = PersistedInstance(Key);
        _instanceRepository.FindByIdentifierForDataAppendAsync(Key, Arg.Any<CancellationToken>()).Returns(existing);
        _instanceRepository.FindDataVersionAsync(existing.Id, "1.0.0", Arg.Any<CancellationToken>()).Returns("1.0.0");

        var result = await _sut.PublishAsync(FlowInput("1.0.0"));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConflictWorkflow);
        await _instanceRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default);
        await _dataWriter.DidNotReceiveWithAnyArgs()
            .AppendExplicitAsync(default!, default, default!, default!, default);
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindByIdentifierWithFullDataAsync(default, default);
    }

    [Fact]
    public async Task Publishing_a_new_version_appends_it_through_the_funnel_with_the_tags()
    {
        var existing = PersistedInstance(Key);
        _instanceRepository.FindByIdentifierForDataAppendAsync(Key, Arg.Any<CancellationToken>()).Returns(existing);
        _instanceRepository.FindDataVersionAsync(existing.Id, "1.1.0", Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await _sut.PublishAsync(FlowInput("1.1.0", tags: ["blue"]));

        result.IsSuccess.ShouldBeTrue();
        existing.Tags.ShouldBe(["blue"]);
        await _instanceRepository.Received(1).UpdateAsync(existing, true, Arg.Any<CancellationToken>());
        await _dataWriter.Received(1).AppendExplicitAsync(
            existing, Arg.Any<Guid>(), "1.1.0", Arg.Any<JsonData>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindByIdentifierWithFullDataAsync(default, default);
    }

    [Fact]
    public async Task First_publish_inserts_without_a_version_lookup()
    {
        _instanceRepository.FindByIdentifierForDataAppendAsync(Key, Arg.Any<CancellationToken>()).Returns((Instance?)null);

        var result = await _sut.PublishAsync(FlowInput("1.0.0"));

        result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.ReceivedWithAnyArgs(1).InsertAsync(default!, default, default);
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindDataVersionAsync(default, default, default);
    }

    [Fact]
    public async Task A_seed_item_whose_version_exists_is_skipped()
    {
        var seed = PersistedInstance("seed-a");
        _instanceRepository.FindByIdentifierForDataAppendAsync(Key, Arg.Any<CancellationToken>()).Returns((Instance?)null);
        _instanceRepository.FindByIdentifierForDataAppendAsync("seed-a", Arg.Any<CancellationToken>()).Returns(seed);
        _instanceRepository.FindDataVersionAsync(seed.Id, "2.0.0", Arg.Any<CancellationToken>()).Returns("2.0.0");

        var input = FlowInput("1.0.0");
        input.Data =
        [
            new PublishDataInput
            {
                Key = "seed-a",
                Version = "2.0.0",
                Attributes = JsonDocument.Parse("""{"name":"a"}""").RootElement.Clone()
            }
        ];

        var result = await _sut.PublishAsync(input);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriter.DidNotReceive().AppendExplicitAsync(
            seed, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<JsonData>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive().UpdateAsync(seed, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidating_an_unknown_version_answers_not_found()
    {
        var existing = PersistedInstance(Key);
        _instanceRepository.FindByIdentifierForDataAppendAsync(Key, Arg.Any<CancellationToken>()).Returns(existing);
        _instanceRepository.FindDataByVersionAsync(existing.Id, "9.9.9", Arg.Any<CancellationToken>())
            .Returns((InstanceData?)null);

        var result = await _sut.InvalidateCacheAsync(new InvalidateCacheInput
        {
            Key = Key, Domain = "test-domain", Flow = RuntimeSysSchemaInfo.Flows, Version = "9.9.9"
        });

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.NotFoundInstanceData);
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindByIdentifierWithFullDataAsync(default, default);
    }

    [Fact]
    public async Task Invalidating_a_version_recasts_the_one_row_it_resolves_to()
    {
        var existing = PersistedInstance(Key);
        var row = new InstanceData(Guid.NewGuid(), existing.Id, "1.0.0", new JsonData("""{"type":"F"}"""), true);
        _instanceRepository.FindByIdentifierForDataAppendAsync(Key, Arg.Any<CancellationToken>()).Returns(existing);
        _instanceRepository.FindDataByVersionAsync(existing.Id, "1.0.0", Arg.Any<CancellationToken>()).Returns(row);

        var result = await _sut.InvalidateCacheAsync(new InvalidateCacheInput
        {
            Key = Key, Domain = "test-domain", Flow = RuntimeSysSchemaInfo.Flows, Version = "1.0.0"
        });

        result.IsSuccess.ShouldBeTrue();
        await _castHandler.Received(1).HandleAsync(
            Arg.Is<IReference>(r => r.Key == Key && r.Version == "1.0.0"),
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An aggregate as EF materializes it: <see cref="Instance.IsTransient"/> is false. There is no
    /// mutator by design, so a unit test stands in for the materialization with reflection.
    /// </summary>
    private static Instance PersistedInstance(string key)
    {
        var instance = Instance.Create(Guid.NewGuid(), RuntimeSysSchemaInfo.Flows, "1.0.0", key);
        typeof(Instance).GetProperty(nameof(Instance.IsTransient))!.SetValue(instance, false);
        return instance;
    }

    private static PublishInput FlowInput(string version, List<string>? tags = null)
    {
        const string json = """
            {
                "type": "F",
                "labels": [{"label": "Test", "language": "en"}],
                "states": [
                    {
                        "key": "review",
                        "stateType": 2,
                        "versionStrategy": "Minor",
                        "labels": [{"label": "Review", "language": "en"}],
                        "transitions": []
                    }
                ],
                "sharedTransitions": [],
                "startTransition": {
                    "key": "start",
                    "target": "review",
                    "versionStrategy": "Minor",
                    "triggerType": 0,
                    "labels": [{"label": "Start", "language": "en"}]
                }
            }
            """;

        return new PublishInput
        {
            Key = Key,
            Domain = "test-domain",
            Flow = RuntimeSysSchemaInfo.Flows,
            FlowVersion = "1.0.0",
            Version = version,
            Tags = tags ?? [],
            Attributes = JsonDocument.Parse(json).RootElement.Clone()
        };
    }
}
