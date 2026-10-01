using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// <see cref="SubItemEventDataResolver"/>: a SubFlow/SubProcess opens its own <c>encrypt</c> values before handing its data
/// to the parent; everything else — a root, a row without tokens, a host without the protector — hands the row as stored.
/// </summary>
public sealed class SubItemEventDataResolverTests
{
    private const string Token = "ENCRYPTED:AES256:i1:AQIDBAUGBwgJCgsMDQ4PEBESExQ";

    private static Instance Seeded(bool subItem, string json)
    {
        var instance = InstanceFactory.CreateDefault("child");
        if (subItem)
            instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
        instance.SeedStoredData(json);
        return instance;
    }

    private static IInstanceDataProtector Protector()
    {
        var protector = Substitute.For<IInstanceDataProtector>();
        protector.UnprotectAsync(Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<JsonData>(), Arg.Any<CancellationToken>())
            .Returns(new InstanceDataView(new JsonData("""{ "email": "user@example.com" }"""),
                new Dictionary<string, string> { ["email"] = Token }));
        return protector;
    }

    [Fact]
    public async Task ASubItemWithTokens_HandsItsOpenedData()
    {
        var data = await new SubItemEventDataResolver(Protector())
            .ResolveAsync(Seeded(subItem: true, $$"""{ "email": "{{Token}}" }"""));

        data!.Value.GetProperty("email").GetString().ShouldBe("user@example.com");
    }

    [Fact]
    public async Task ARoot_ARowWithoutTokens_AndNoProtector_HandTheRowAsStored()
    {
        var protector = Protector();

        (await new SubItemEventDataResolver(protector).ResolveAsync(Seeded(false, $$"""{ "email": "{{Token}}" }""")))
            .ShouldBeNull();
        (await new SubItemEventDataResolver(protector).ResolveAsync(Seeded(true, """{ "email": "plain" }""")))
            .ShouldBeNull();
        (await new SubItemEventDataResolver().ResolveAsync(Seeded(true, $$"""{ "email": "{{Token}}" }""")))
            .ShouldBeNull();
        await protector.DidNotReceiveWithAnyArgs().UnprotectAsync(default, default, default!, default);
    }
}
