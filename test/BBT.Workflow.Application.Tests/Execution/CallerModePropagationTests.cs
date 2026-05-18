using System;
using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution;

/// <summary>
/// Tests that CallerMode propagates correctly through the execution pipeline,
/// separating pipeline execution mode (Mode) from the original caller's sync/async intent (CallerMode).
/// </summary>
public class CallerModePropagationTests
{
    [Fact]
    public void TransitionInput_ToExecutionContext_SyncTrue_SetsBothModeAndCallerModeToSync()
    {
        var input = new TransitionInput("domain", "workflow", sync: true);

        var context = input.ToExecutionContext("inst-1", "1.0.0", "trans-1");

        context.Mode.ShouldBe(ExecMode.Sync);
        context.CallerMode.ShouldBe(ExecMode.Sync);
    }

    [Fact]
    public void TransitionInput_ToExecutionContext_SyncFalse_SetsBothModeAndCallerModeToAsync()
    {
        var input = new TransitionInput("domain", "workflow", sync: false);

        var context = input.ToExecutionContext("inst-1", "1.0.0", "trans-1");

        context.Mode.ShouldBe(ExecMode.Async);
        context.CallerMode.ShouldBe(ExecMode.Async);
    }

    [Fact]
    public void StartInstanceInput_ToExecutionContext_SyncTrue_SetsBothModeAndCallerModeToSync()
    {
        var input = new StartInstanceInput("domain", "workflow", sync: true)
        {
            Instance = new CreateInstanceInput { Attributes = null }
        };

        var context = input.ToExecutionContext(Guid.NewGuid(), "start");

        context.Mode.ShouldBe(ExecMode.Sync);
        context.CallerMode.ShouldBe(ExecMode.Sync);
    }

    [Fact]
    public void StartInstanceInput_ToExecutionContext_SyncFalse_SetsBothModeAndCallerModeToAsync()
    {
        var input = new StartInstanceInput("domain", "workflow", sync: false)
        {
            Instance = new CreateInstanceInput { Attributes = null }
        };

        var context = input.ToExecutionContext(Guid.NewGuid(), "start");

        context.Mode.ShouldBe(ExecMode.Async);
        context.CallerMode.ShouldBe(ExecMode.Async);
    }

    [Fact]
    public void TransitionJobPayload_CallerSync_DefaultsToFalse()
    {
        var payload = new TransitionJobPayload();
        payload.CallerSync.ShouldBeFalse();
    }

    [Fact]
    public void WorkflowExecutionContext_CallerMode_CanDifferFromMode()
    {
        var context = new WorkflowExecutionContext
        {
            Mode = ExecMode.Sync,
            CallerMode = ExecMode.Async,
            Domain = "d",
            WorkflowKey = "w",
            TransitionKey = "t",
            InstanceId = Guid.NewGuid().ToString()
        };

        context.Mode.ShouldBe(ExecMode.Sync);
        context.CallerMode.ShouldBe(ExecMode.Async);
    }

    [Fact]
    public void TransitionExecutionContext_CallerMode_CanDifferFromMode()
    {
        var context = new TransitionExecutionContext
        {
            Mode = ExecMode.Sync,
            CallerMode = ExecMode.Async,
            Domain = "d",
            WorkflowKey = "w",
            TransitionKey = "t",
            InstanceId = Guid.NewGuid()
        };

        context.Mode.ShouldBe(ExecMode.Sync);
        context.CallerMode.ShouldBe(ExecMode.Async);
    }
}
