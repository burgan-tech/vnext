using System;
using BBT.Aether;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// <see cref="Instance.Type"/> — how an instance was STARTED (<c>R</c> root, <c>S</c> SubFlow child,
/// <c>P</c> SubProcess child), stamped once at creation and never updated.
/// <para>
/// The derivation is gated on <c>parent.id</c>, not on <c>parent.flowtype</c>, and that is the whole
/// point: <see cref="Instance.SetInfoMetadata"/> <c>TryAdd</c>s the instance's OWN workflow type
/// code into <c>parent.flowtype</c> when the key is absent, so a workflow whose definition declares
/// <c>type: "S"</c> and is started directly through the API carries <c>parent.flowtype: "S"</c> with
/// no parent at all. Several real vnext-example workflows are in exactly that shape.
/// </para>
/// <para>
/// This field is purely additive. <see cref="Instance.IsSubFlow"/> / <see cref="Instance.IsSubItem"/>
/// and every <c>parent.*</c> reader keep their existing behaviour, including the mislabel above;
/// the tests below pin both sides so a future change cannot quietly unify them.
/// </para>
/// </summary>
public class InstanceTypeTests : DomainTestBase<DomainEntryPoint>
{
    private static ExtraPropertyDictionary ParentBlock(string flowType, Guid? parentId = null)
    {
        return new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = parentId ?? Guid.NewGuid(),
            [DomainConsts.MetaDataKeys.Key] = "parent-key",
            [DomainConsts.MetaDataKeys.Domain] = "core",
            [DomainConsts.MetaDataKeys.Flow] = "parent-flow",
            [DomainConsts.MetaDataKeys.Version] = "1.0.0",
            [DomainConsts.MetaDataKeys.State] = "awaiting-sub",
            [DomainConsts.MetaDataKeys.Transition] = "start-sub",
            [DomainConsts.MetaDataKeys.FlowType] = flowType,
            [DomainConsts.MetaDataKeys.RootInstanceId] = Guid.NewGuid()
        };
    }

    /// <summary>
    /// A start-trigger task child and a plain API start both write no parent block at all.
    /// </summary>
    [Fact]
    public void WithNoParentBlock_IsRoot()
    {
        var instance = InstanceFactory.CreateDefault();

        instance.SetInfoMetadata(isSync: false, callback: null, flowType: "F");

        instance.Type.ShouldBe(InstanceType.Root);
    }

    /// <summary>The SubflowStarter path, kind taken from the SubFlow definition's own type.</summary>
    [Theory]
    [InlineData("S")]
    [InlineData("P")]
    public void WithAParentBlock_TakesTheKindFromFlowType(string flowType)
    {
        var instance = InstanceFactory.CreateDefault();

        instance.SetInfoMetadata(isSync: true, callback: null, flowType: "S", ParentBlock(flowType));

        instance.Type.ShouldBe(InstanceType.FromCode(flowType));
    }

    /// <summary>
    /// A cross-domain child arrives through <c>POST …/sub/instances/start</c>, which forwards the
    /// parent block verbatim — so it must classify identically to a local child.
    /// </summary>
    [Theory]
    [InlineData("S")]
    [InlineData("P")]
    public void ACrossDomainChild_ClassifiesFromTheForwardedBlock(string flowType)
    {
        var forwarded = ParentBlock(flowType);
        var instance = InstanceFactory.CreateDefault();

        // The controller copies the remote body's ExtraProperties into CreateInstanceInput and the
        // app service hands them straight to SetInfoMetadata, exactly as below.
        instance.SetInfoMetadata(isSync: true, callback: null, flowType: "S",
            new ExtraPropertyDictionary(forwarded));

        instance.Type.ShouldBe(InstanceType.FromCode(flowType));
    }

    /// <summary>
    /// The load-bearing case. A definition of type "S" started as a root: SetInfoMetadata TryAdds
    /// "S" into parent.flowtype, so IsSubFlow answers true — and Type must still say Root. This is
    /// simultaneously the reason parent.flowtype alone is unsafe and the pin that this change stayed
    /// additive: the IsSubFlow assertion below is the EXISTING behaviour and must not change here.
    /// </summary>
    [Fact]
    public void ASubFlowDefinitionStartedAsARoot_IsRootEvenThoughIsSubFlowSaysOtherwise()
    {
        var instance = InstanceFactory.CreateDefault();

        instance.SetInfoMetadata(isSync: false, callback: null, flowType: "S");

        instance.Type.ShouldBe(InstanceType.Root);
        instance.IsSubFlow.ShouldBeTrue();
        instance.IsSubItem.ShouldBeTrue();
    }

    /// <summary>
    /// An empty or unparsable parent id is no parent. Guards the Guid.Empty case specifically,
    /// which <c>SubFlowContractInfo</c> also produces for a missing key.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public void WithAnUnusableParentId_IsRoot(string parentId)
    {
        var metadata = ParentBlock("S");
        metadata[DomainConsts.MetaDataKeys.Id] = parentId;
        var instance = InstanceFactory.CreateDefault();

        instance.SetInfoMetadata(isSync: false, callback: null, flowType: "S", metadata);

        instance.Type.ShouldBe(InstanceType.Root);
    }

    /// <summary>
    /// A parent id with a flow type that is not a child kind ("C"/"F", or the key missing outright).
    /// Pins the one-directional invariant: Type != Root implies IsSubItem, but NOT the converse —
    /// see <see cref="ASubFlowDefinitionStartedAsARoot_IsRootEvenThoughIsSubFlowSaysOtherwise"/>.
    /// Both directions are asserted so nobody "simplifies" one into the other.
    /// </summary>
    [Theory]
    [InlineData("C")]
    [InlineData("F")]
    public void WithAParentIdButANonChildFlowType_IsRoot(string flowType)
    {
        var instance = InstanceFactory.CreateDefault();

        instance.SetInfoMetadata(isSync: false, callback: null, flowType: "C", ParentBlock(flowType));

        instance.Type.ShouldBe(InstanceType.Root);
        instance.IsSubItem.ShouldBeFalse();
    }

    [Fact]
    public void ANonRootTypeAlwaysImpliesIsSubItem()
    {
        foreach (var flowType in new[] { "S", "P" })
        {
            var instance = InstanceFactory.CreateDefault();
            instance.SetInfoMetadata(isSync: true, callback: null, flowType: "S", ParentBlock(flowType));

            instance.Type.ShouldNotBe(InstanceType.Root);
            instance.IsSubItem.ShouldBeTrue();
        }
    }

    /// <summary>
    /// The script context's instance IS a snapshot, so a snapshot that drops the field makes every
    /// .csx read the constructor's Root default. EffectiveStatus was shipped with exactly this bug.
    /// </summary>
    [Fact]
    public void Snapshot_CarriesTheType()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.SetInfoMetadata(isSync: true, callback: null, flowType: "S", ParentBlock("S"));

        instance.CreateSnapshot().Type.ShouldBe(InstanceType.SubFlow);
    }

    /// <summary>
    /// SetMetaData replaces ExtraProperties wholesale, which is precisely why Type is a stored
    /// column and not a property computed over that dictionary.
    /// </summary>
    [Fact]
    public void SetMetaData_CannotReclassify()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.SetInfoMetadata(isSync: true, callback: null, flowType: "S", ParentBlock("S"));

        instance.SetMetaData(new ExtraPropertyDictionary());

        instance.Type.ShouldBe(InstanceType.SubFlow);
    }

    /// <summary>
    /// TrackResourceLock is the only production caller of SetMetaData.
    /// </summary>
    [Fact]
    public void TrackResourceLock_DoesNotReclassify()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.SetInfoMetadata(isSync: true, callback: null, flowType: "P", ParentBlock("P"));

        instance.TrackResourceLock("vnext:core:kyc:42");

        instance.Type.ShouldBe(InstanceType.SubProcess);
        instance.GetTrackedResourceLocks().ShouldContain("vnext:core:kyc:42");
    }

    /// <summary>
    /// The IsTransient latch: an aggregate loaded from the database can never be reclassified, so a
    /// stray SetInfoMetadata cannot rewrite a persisted row's origin.
    /// </summary>
    /// <remarks>
    /// IsTransient is set only by the creating constructor and is Ignore()d in the EF model, so an
    /// EF-materialized aggregate has it false. There is no mutator by design; the reflection below
    /// is the only way a unit test can stand in for that materialization.
    /// </remarks>
    [Fact]
    public void SetInfoMetadata_OnAPersistedAggregate_CannotReclassify()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.SetInfoMetadata(isSync: true, callback: null, flowType: "S", ParentBlock("S"));
        typeof(Instance)
            .GetProperty(nameof(Instance.IsTransient))!
            .SetValue(instance, false);

        instance.SetInfoMetadata(isSync: true, callback: null, flowType: "P", ParentBlock("P"));

        instance.Type.ShouldBe(InstanceType.SubFlow);
    }

    [Fact]
    public void Metadata_SerializesTypeAsABareCode()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.SetInfoMetadata(isSync: true, callback: null, flowType: "S", ParentBlock("S"));

        var json = System.Text.Json.JsonSerializer.Serialize(
            new InstanceMetadataDto(instance),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        json.ShouldContain("\"type\":\"S\"");
    }

    [Theory]
    [InlineData("R")]
    [InlineData("S")]
    [InlineData("P")]
    public void FromCode_RoundTrips(string code)
    {
        InstanceType.FromCode(code).Code.ShouldBe(code);
        InstanceType.TryFromCode(code)!.Code.ShouldBe(code);
    }

    [Fact]
    public void FromCode_ThrowsOnAnUnknownCode_WhileTryFromCodeAnswersNull()
    {
        Should.Throw<ArgumentException>(() => InstanceType.FromCode("X"));
        InstanceType.TryFromCode("X").ShouldBeNull();
        InstanceType.TryFromCode(null).ShouldBeNull();
    }

    [Fact]
    public void FromStartMetadata_TreatsNullMetadataAsRoot()
    {
        InstanceType.FromStartMetadata(null).ShouldBe(InstanceType.Root);
        InstanceType.FromStartMetadata(new ExtraPropertyDictionary()).ShouldBe(InstanceType.Root);
    }
}
