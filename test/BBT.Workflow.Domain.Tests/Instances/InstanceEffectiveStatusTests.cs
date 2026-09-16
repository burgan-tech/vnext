using System;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// <see cref="Instance.GetEffectiveStatus"/> — the status every instance projection serves as
/// <c>metadata.effectiveStatus</c>, and the value that must agree with the state function's own
/// <c>status</c> in every regime.
/// <para>
/// The raw <see cref="Instance.EffectiveStatus"/> column is the propagated value; this accessor is
/// the answer. They differ in exactly one window — a child that has come to rest and stamped its
/// terminal status upward while this level's correlation is still open and this level is still
/// running. The state function already answers <c>Busy</c> there (its <c>subFlowIsTerminal</c>
/// guard); serving the raw <c>C</c> would tell a client the flow finished mid-resume.
/// </para>
/// </summary>
public class InstanceEffectiveStatusTests : DomainTestBase<DomainEntryPoint>
{
    private static Instance WithActiveSubFlow(Instance instance, out Guid subInstanceId)
    {
        subInstanceId = Guid.NewGuid();
        instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "awaiting-sub",
            subInstanceId,
            SubFlowType.SubFlow.Code,
            "compliance",
            "kyc-check",
            "1.0.0"));
        return instance;
    }

    [Fact]
    public void WithNoSubFlow_TheProjectionIsTheInstancesOwnStatus()
    {
        var instance = InstanceFactory.CreateDefault();

        instance.Status.ShouldBe(InstanceStatus.Active);
        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Active);
    }

    [Fact]
    public void WithNoSubFlow_ATerminalOwnStatusIsServedAsIs()
    {
        var instance = InstanceFactory.CreateDefault();

        instance.Complete("core");

        instance.Status.ShouldBe(InstanceStatus.Completed);
        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Completed);
    }

    /// <summary>
    /// The whole point of the field: the parent is Busy for the child's entire lifetime by design,
    /// so its own status carries no information while the child waits on a human task.
    /// </summary>
    [Fact]
    public void WithAnActiveSubFlow_TheChildsNonTerminalStatusIsServed()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.Active);

        instance.Status.ShouldBe(InstanceStatus.Busy);
        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Active);
    }

    [Fact]
    public void WithAnActiveSubFlow_ABusyChildIsServedAsBusy()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.Busy);

        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Busy);
    }

    /// <summary>
    /// The completion window. The child published <c>C</c> at its rest point, the parent's
    /// correlation is still open and the parent is still Busy resuming. Serving the raw column here
    /// would contradict the state function, which falls back to the main flow and answers Busy.
    /// </summary>
    [Theory]
    [InlineData("C")]
    [InlineData("F")]
    [InlineData("P")]
    public void InTheCompletionWindow_ATerminalChildStatusIsClampedToTheOwnStatus(string terminalCode)
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.FromCode(terminalCode));

        instance.Status.ShouldBe(InstanceStatus.Busy);
        instance.EffectiveStatus.Code.ShouldBe(terminalCode);
        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Busy);
    }

    /// <summary>
    /// The clamp is not "never terminal": once THIS level is terminal too, the terminal answer is
    /// the truth and must survive.
    /// </summary>
    [Fact]
    public void OnceThisLevelIsTerminalToo_TheTerminalStatusSurvivesTheClamp()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.Faulted);

        instance.Fault("core");

        instance.Status.ShouldBe(InstanceStatus.Faulted);
        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Faulted);
    }

    /// <summary>
    /// The mirror of the completion window, and the one the write side cannot fix: a cancel or fault
    /// cascade completes this level while the child's correlation is still open, so
    /// <see cref="Instance.ResyncEffectiveStatus"/> no-ops, and cleanup closes the correlation
    /// afterwards with nothing left to restamp the column. It stays on the child's last non-terminal
    /// status forever. The state function reports this level's own status there (its descent finds no
    /// active correlation), so the clamp must too — measured against 15 pre-existing cancelled rows
    /// that served effectiveStatus "A" while the state function said "C".
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void OnATerminalLevel_AStaleNonTerminalProjectionLosesToTheOwnStatus(string staleCode)
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.FromCode(staleCode));

        instance.Cancel("core");

        instance.Status.ShouldBe(InstanceStatus.Completed);
        instance.EffectiveStatus.Code.ShouldBe(staleCode);
        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Completed);
    }

    /// <summary>
    /// The write-side counterpart of the clamp: the SubFlow terminal paths call this after resetting
    /// EffectiveState, so the COLUMN — which is state-function fingerprint material — stops
    /// describing a chain that has already closed.
    /// </summary>
    [Fact]
    public void ResyncEffectiveStatus_PutsTheColumnBackOnceTheCorrelationIsClosed()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out var subInstanceId);
        instance.SetEffectiveStatus(InstanceStatus.Completed);

        instance.CompleteCorrelation(subInstanceId);
        instance.ResyncEffectiveStatus();

        instance.EffectiveStatus.ShouldBe(InstanceStatus.Busy);
        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Busy);
    }

    /// <summary>
    /// A second still-open SubFlow keeps owning the projection — the resync must not steal it.
    /// </summary>
    [Fact]
    public void ResyncEffectiveStatus_IsANoOpWhileAnotherSubFlowIsStillOpen()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out var firstSubInstanceId);
        WithActiveSubFlow(instance, out _);
        instance.SetEffectiveStatus(InstanceStatus.Active);

        instance.CompleteCorrelation(firstSubInstanceId);
        instance.ResyncEffectiveStatus();

        instance.EffectiveStatus.ShouldBe(InstanceStatus.Active);
    }

    /// <summary>
    /// The script context's instance IS a snapshot; without this the rule/role-grant surfaces read
    /// the constructor's Active default instead of the client-visible status.
    /// </summary>
    [Fact]
    public void CreateSnapshot_CarriesTheProjection()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.Busy);

        var snapshot = instance.CreateSnapshot();

        snapshot.EffectiveStatus.ShouldBe(InstanceStatus.Busy);
        snapshot.GetEffectiveStatus.ShouldBe(InstanceStatus.Busy);
    }

    [Theory]
    [InlineData("C", true)]
    [InlineData("F", true)]
    [InlineData("P", true)]
    [InlineData("A", false)]
    [InlineData("B", false)]
    public void IsTerminal_IsTheSingleDefinitionBothTheClampAndTheStateFunctionRead(string code, bool expected)
        => InstanceStatus.FromCode(code).IsTerminal.ShouldBe(expected);

    /// <summary>
    /// The DTO contract: <c>metadata.effectiveStatus</c> serves the CLAMPED accessor, never the raw
    /// column. One constructor feeds the single instance GET, every item of the list view and
    /// <c>GetInstanceTask</c>, so this is the whole served surface in one assertion.
    /// </summary>
    [Fact]
    public void Metadata_ServesTheClampedProjection_NotTheRawColumn()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.Completed);

        var metadata = new InstanceMetadataDto(instance);

        metadata.Status.ShouldBe(InstanceStatus.Busy);
        metadata.EffectiveStatus.ShouldBe(InstanceStatus.Busy);
        metadata.EffectiveStatus.ShouldNotBe(instance.EffectiveStatus);
    }

    [Fact]
    public void Metadata_CarriesTheChildsStatusWhileASubFlowIsRunning()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.Active);

        var metadata = new InstanceMetadataDto(instance);

        metadata.Status.ShouldBe(InstanceStatus.Busy);
        metadata.EffectiveStatus.ShouldBe(InstanceStatus.Active);
    }

    /// <summary>
    /// Without a subflow the two metadata fields are the same value by construction — a client that
    /// only ever reads <c>effectiveStatus</c> is never worse off than one reading <c>status</c>.
    /// </summary>
    [Fact]
    public void Metadata_CollapsesToTheOwnStatusWhenNoSubFlowIsRunning()
    {
        var metadata = new InstanceMetadataDto(InstanceFactory.CreateDefault());

        metadata.EffectiveStatus.ShouldBe(metadata.Status);
    }

    /// <summary>
    /// The wire shape: <c>effectiveStatus</c> serializes as the bare status code, exactly like
    /// <c>status</c> next to it. <see cref="InstanceStatus"/> carries a custom converter, and the new
    /// member is nullable where the aggregate's is not — this pins that neither difference leaks into
    /// the JSON a client parses.
    /// </summary>
    [Fact]
    public void Metadata_SerializesEffectiveStatusAsABareCode()
    {
        var instance = WithActiveSubFlow(InstanceFactory.CreateDefault(), out _);
        instance.SetEffectiveStatus(InstanceStatus.Active);

        var json = System.Text.Json.JsonSerializer.Serialize(
            new InstanceMetadataDto(instance),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        json.ShouldContain("\"status\":\"B\"");
        json.ShouldContain("\"effectiveStatus\":\"A\"");
    }
}
