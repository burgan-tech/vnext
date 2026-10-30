using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using BBT.Aether.Events;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Events;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

public class InstanceSubItemCarriedHeadersTests : DomainTestBase<DomainEntryPoint>
{
    private static Dictionary<string, string?> Headers() => new() { ["sub"] = "u1" };

    private static Instance NewSubFlowChild()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Id] = Guid.NewGuid().ToString();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Domain] = "test-domain";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Flow] = "parent-flow";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Version] = "1.0.0";
        return instance;
    }

    private static IEnumerable<object> Events(Instance instance) =>
        instance.GetDomainEvents().Select(e => (object)e.Event);

    private static bool CarriesHeaders(object e) => e.GetType().GetProperty("CarriedHeaders") != null;

    [Fact]
    public void Complete_Stamps_CarriedHeaders_On_SubCompletedEvent_Only()
    {
        var instance = NewSubFlowChild();

        instance.Complete("child-domain", carriedHeaders: Headers());

        var events = Events(instance).ToList();
        events.OfType<InstanceSubCompletedEvent>().Single().CarriedHeaders.ShouldBe(Headers());
        typeof(InstanceCompletedCleanupEvent).GetProperty("CarriedHeaders").ShouldBeNull();
        events.Where(e => e is not InstanceSubCompletedEvent).ShouldAllBe(e => !CarriesHeaders(e));
    }

    [Fact]
    public void Fault_And_Cancel_Stamp_CarriedHeaders()
    {
        var faulted = NewSubFlowChild();
        faulted.Fault("child-domain", carriedHeaders: Headers());
        Events(faulted).OfType<InstanceSubFaultedEvent>().Single().CarriedHeaders.ShouldBe(Headers());
        Events(faulted).Where(e => e is not InstanceSubFaultedEvent).ShouldAllBe(e => !CarriesHeaders(e));

        var canceled = NewSubFlowChild();
        canceled.Cancel("child-domain", carriedHeaders: Headers());
        Events(canceled).OfType<InstanceSubCanceledEvent>().Single().CarriedHeaders.ShouldBe(Headers());
        Events(canceled).Where(e => e is not InstanceSubCanceledEvent).ShouldAllBe(e => !CarriesHeaders(e));
    }

    [Fact]
    public void Null_CarriedHeaders_Stays_Null()
    {
        var completed = NewSubFlowChild();
        completed.Complete("child-domain");
        Events(completed).OfType<InstanceSubCompletedEvent>().Single().CarriedHeaders.ShouldBeNull();

        var faulted = NewSubFlowChild();
        faulted.Fault("child-domain");
        Events(faulted).OfType<InstanceSubFaultedEvent>().Single().CarriedHeaders.ShouldBeNull();

        var canceled = NewSubFlowChild();
        canceled.Cancel("child-domain");
        Events(canceled).OfType<InstanceSubCanceledEvent>().Single().CarriedHeaders.ShouldBeNull();
    }

    [Fact]
    public void Event_ToString_Does_Not_Contain_Header_Values()
    {
        var secret = new Dictionary<string, string?> { ["sub"] = "secret-value-123" };
        var completed = NewSubFlowChild();
        completed.Complete("child-domain", carriedHeaders: secret);
        var faulted = NewSubFlowChild();
        faulted.Fault("child-domain", carriedHeaders: secret);
        var canceled = NewSubFlowChild();
        canceled.Cancel("child-domain", carriedHeaders: secret);

        foreach (var e in Events(completed).OfType<InstanceSubCompletedEvent>().Cast<object>()
                     .Concat(Events(faulted).OfType<InstanceSubFaultedEvent>())
                     .Concat(Events(canceled).OfType<InstanceSubCanceledEvent>()))
        {
            e.ToString().ShouldNotContain("secret-value-123");
            e.ToString().ShouldNotContain("CarriedHeaders");
        }
    }

    // Runtime event path: Aether's registered IEventSerializer is SystemTextJsonEventSerializer
    // (aether/framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/SystemTextJsonEventSerializer.cs:12-15,
    // registered by AetherEventBusServiceCollectionExtensions.cs:20; camelCase naming only).
    // The Inbox -> /complete HTTP body is read by MVC with JsonSerializerConstants.JsonOptions
    // (WorkflowApiBaseServiceCollectionExtensions.cs:88-105 copies it into AddJsonOptions).
    // Domain.Tests cannot reference the Application DTO (HttpBodyProbe), so the HTTP side reads the body
    // into a probe carrying just the new member; the real DTO round trip is Application.Tests' concern.
    private sealed record HttpBodyProbe(Dictionary<string, string?>? CarriedHeaders);

    private static readonly IEventSerializer EventSerializer = new SystemTextJsonEventSerializer();

    private static byte[] EventJson(Instance instance) =>
        EventSerializer.Serialize(Events(instance).OfType<InstanceSubCompletedEvent>().Single());

    [Fact]
    public void Old_Json_Without_Field_Deserializes_To_Null()
    {
        var instance = NewSubFlowChild();
        instance.Complete("child-domain", carriedHeaders: Headers());
        var old = JsonSerializer.Deserialize<JsonElement>(EventJson(instance)).EnumerateObject()
            .Where(p => !string.Equals(p.Name, nameof(InstanceSubCompletedEvent.CarriedHeaders),
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Name, p => p.Value);
        var oldBytes = JsonSerializer.SerializeToUtf8Bytes(old);

        var viaBus = EventSerializer.Deserialize<InstanceSubCompletedEvent>(oldBytes);
        var viaHttp = JsonSerializer.Deserialize<HttpBodyProbe>(oldBytes, JsonSerializerConstants.JsonOptions);

        viaBus.ShouldNotBeNull();
        viaBus.CarriedHeaders.ShouldBeNull();
        viaHttp.ShouldNotBeNull();
        viaHttp.CarriedHeaders.ShouldBeNull();
    }

    [Fact]
    public void Carried_Headers_Round_Trip_Through_Event_Bus_And_Http_Serializers()
    {
        var instance = NewSubFlowChild();
        instance.Complete("child-domain", carriedHeaders: Headers());
        var bytes = EventJson(instance);

        EventSerializer.Deserialize<InstanceSubCompletedEvent>(bytes)!.CarriedHeaders.ShouldBe(Headers());
        JsonSerializer.Deserialize<HttpBodyProbe>(bytes, JsonSerializerConstants.JsonOptions)!
            .CarriedHeaders.ShouldBe(Headers());
    }

    private sealed record V1Shape(Guid InstanceId, Guid SubInstanceId, string? Domain, string? Flow, string? CompletedState);

    [Fact]
    public void New_Json_Deserializes_Into_Old_Shape()
    {
        var instance = NewSubFlowChild();
        instance.Complete("child-domain", carriedHeaders: Headers());
        var bytes = EventJson(instance);

        var viaBus = Should.NotThrow(() => EventSerializer.Deserialize<V1Shape>(bytes));
        var viaHttp = Should.NotThrow(() => JsonSerializer.Deserialize<V1Shape>(bytes, JsonSerializerConstants.JsonOptions));

        viaBus.ShouldNotBeNull();
        viaBus.Domain.ShouldBe("test-domain");
        viaHttp.ShouldNotBeNull();
    }
}
