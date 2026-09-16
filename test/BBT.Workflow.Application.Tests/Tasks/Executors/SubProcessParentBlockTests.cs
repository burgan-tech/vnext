using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBT.Aether;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Runtime;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// The parent block a SubProcess trigger task writes is the child's only classification input, so
/// it must carry a usable <c>parent.id</c> and the <c>P</c> flow type — otherwise the child is
/// stamped <see cref="InstanceType.Root"/> and every report loses it.
/// </summary>
/// <remarks>
/// <see cref="SubProcessTaskExecutor"/> has TWO near-identical builders — one for the local
/// <c>StartInstanceInput</c> and one for the remote binding — and nothing but this test keeps them
/// from drifting apart. Both are private statics, hence the reflection.
/// </remarks>
public class SubProcessParentBlockTests
{
    private static ScriptContext BuildContext()
    {
        var workflow = WorkflowFactory.CreateDefault();
        var instance = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent-key");
        instance.ChangeState(
            State.Create("awaiting-documents", StateType.Intermediate, StateSubType.None, "Patch"));

        return new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetWorkflow(workflow)
            .SetInstance(instance)
            .SetTransition(TransitionFactory.CreateDefault())
            .Build();
    }

    private static object Invoke(string methodName, ScriptContext context)
    {
        var method = typeof(SubProcessTaskExecutor).GetMethod(
                         methodName, BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException($"{methodName} not found.");

        return method.Invoke(null, [context, null])!;
    }

    [Fact]
    public void BuildExtraProperties_ClassifiesTheChildAsASubProcess()
    {
        var context = BuildContext();

        var metadata = (ExtraPropertyDictionary)Invoke("BuildExtraProperties", context);

        metadata[DomainConsts.MetaDataKeys.Id].ShouldBe(context.Instance.Id);
        metadata[DomainConsts.MetaDataKeys.FlowType].ShouldBe(SubFlowType.SubProcess.Code);
        InstanceType.FromStartMetadata(metadata).ShouldBe(InstanceType.SubProcess);
    }

    [Fact]
    public void TheRemoteBuilder_EmitsTheSameParentBlockAsTheLocalOne()
    {
        var context = BuildContext();

        var local = (ExtraPropertyDictionary)Invoke("BuildExtraProperties", context);
        var remote = (Dictionary<string, object>)Invoke("BuildExtraPropertiesAsDictionary", context);

        remote.Keys.OrderBy(k => k).ShouldBe(local.Keys.OrderBy(k => k));
        foreach (var (key, value) in remote)
        {
            value.ShouldBe(local[key], $"remote and local parent blocks disagree on '{key}'");
        }

        InstanceType.FromStartMetadata(new ExtraPropertyDictionary(
                remote.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)))
            .ShouldBe(InstanceType.SubProcess);
    }
}
