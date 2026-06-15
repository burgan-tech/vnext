using System;
using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

public sealed class TaskBindingMapperAcceptedStatusCodesTests
{
    [Fact]
    public void CreateEnvelope_MapsDaprServiceAcceptedStatusCodes()
    {
        var task = DaprServiceTask.Create("""
            {
              "appId": "workflow-api",
              "methodName": "/api/v1/test/workflows/order/instances/start",
              "httpVerb": "POST",
              "acceptedStatusCodes": [ "400", "4xx" ]
            }
            """.ToJsonElement());
        task.SetReference(new Reference("start-via-dapr", "test-domain", "sys-tasks", "1.0.0"));

        var binding = CreateBinding<DaprServiceBinding>(task);

        binding.AcceptedStatusCodes.ShouldBe(["400", "4xx"]);
    }

    [Theory]
    [MemberData(nameof(TriggerTasks))]
    public void CreateEnvelope_MapsTriggerTaskAcceptedStatusCodes(
        WorkflowTask task,
        Type bindingType)
    {
        task.SetReference(new Reference(bindingType.Name, "test-domain", "sys-tasks", "1.0.0"));

        var envelope = TaskBindingMapper.CreateEnvelope(task);

        envelope.IsSuccess.ShouldBeTrue();
        var binding = envelope.Value!.Binding.Deserialize(bindingType);
        var acceptedStatusCodes = (IReadOnlyList<string>)bindingType
            .GetProperty("AcceptedStatusCodes")!
            .GetValue(binding)!;
        acceptedStatusCodes.ShouldBe(["400", "4xx"]);
    }

    public static TheoryData<WorkflowTask, Type> TriggerTasks() => new()
    {
        {
            StartTask.Create("""
                {
                  "domain": "test-domain",
                  "flow": "order",
                  "acceptedStatusCodes": [ "400", "4xx" ]
                }
                """.ToJsonElement()),
            typeof(StartTriggerBinding)
        },
        {
            DirectTriggerTask.Create("""
                {
                  "domain": "test-domain",
                  "flow": "order",
                  "transitionName": "approve",
                  "key": "order-1",
                  "acceptedStatusCodes": [ "400", "4xx" ]
                }
                """.ToJsonElement()),
            typeof(DirectTriggerBinding)
        },
        {
            SubProcessTask.Create("""
                {
                  "domain": "test-domain",
                  "flow": "child",
                  "acceptedStatusCodes": [ "400", "4xx" ]
                }
                """.ToJsonElement()),
            typeof(SubProcessBinding)
        },
        {
            GetInstancesTask.Create("""
                {
                  "domain": "test-domain",
                  "flow": "order",
                  "acceptedStatusCodes": [ "400", "4xx" ]
                }
                """.ToJsonElement()),
            typeof(GetInstancesBinding)
        },
        {
            GetInstanceDataTask.Create("""
                {
                  "domain": "test-domain",
                  "flow": "order",
                  "instance": "order-1",
                  "acceptedStatusCodes": [ "400", "4xx" ]
                }
                """.ToJsonElement()),
            typeof(GetInstanceDataBinding)
        }
    };

    private static TBinding CreateBinding<TBinding>(WorkflowTask task)
    {
        var envelope = TaskBindingMapper.CreateEnvelope(task);
        envelope.IsSuccess.ShouldBeTrue();
        return envelope.Value!.Binding.Deserialize<TBinding>()!;
    }
}
