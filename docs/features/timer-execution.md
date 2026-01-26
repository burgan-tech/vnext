# Timer Execution

## Overview

Timer execution schedules workflow transitions using script-based timer definitions and background jobs. A timer script produces a `TimerSchedule`, which is converted into a Dapr-compatible schedule expression and persisted as a background job. When the job fires, the scheduled transition is executed as a re-entry.

## Core Components

### 1. ITimerMapping Contract

Timer scripts implement `ITimerMapping`:

```csharp
public interface ITimerMapping
{
    Task<TimerSchedule> Handler(ScriptContext context);
}
```

### 2. TimerSchedule and TimerScheduleType

`TimerSchedule` is a value object supporting multiple scheduling types:

```csharp
var dateTime = TimerSchedule.FromDateTime(DateTime.UtcNow.AddHours(2));
var cron = TimerSchedule.FromCronExpression("0 9 * * *");
var duration = TimerSchedule.FromDuration(TimeSpan.FromMinutes(30));
var immediate = TimerSchedule.Immediate();

var daily = TimerSchedule.Daily; // "@daily"
var expression = TimerSchedule.FromExpression("@hourly");
```

Supported types:

```csharp
public enum TimerScheduleType
{
    DateTime = 1,
    Cron = 2,
    Duration = 3,
    Immediate = 4
}
```

### 3. Timer Evaluation

Timer scripts are evaluated by `ScriptTimerEvaluator`, which compiles the script into an `ITimerMapping` and executes `Handler`:

```csharp
var scriptRunner = await scriptEngine.CompileToInstanceAsync<ITimerMapping>(script.DecodedCode);
var schedule = await scriptRunner.Handler(context);
```

## Execution Flow

### 1. Scheduling in the Transition Pipeline

`ScheduleTransitionsStep` handles transitions with timers:

- Builds a `ScriptContext` via `IScriptContextFactory`.
- Evaluates the timer with `ITaskTimerService`.
- Converts the schedule to a Dapr expression with `TimerSchedule.ToDaprJobSchedule()`.
- Enqueues a background job and persists `InstanceJob`.

### 2. Background Job Execution

`TransitionTimerJobHandler` runs when the job fires:

- Rebuilds `TransitionExecutionContext` from payload.
- Sets `TriggerType.Scheduled`, `ExecutionActor.System`, and `IsReentry = true`.
- Calls `IWorkflowExecutionService.ExecuteTransitionAsync`.

### 3. Scheduled Transition Handler

`ScheduledTransitionHandler` ensures:

- Initial scheduling skips immediate execution (`IsReentry == false`).
- Execution occurs only on re-entry jobs.

## Script Example

```csharp
public class PaymentDueTimerRule : ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        var frequency = context.Instance.Data.paymentFrequency?.ToString() ?? "monthly";

        return Task.FromResult(frequency switch
        {
            "daily" => TimerSchedule.FromCronExpression("0 9 * * *"),
            "weekly" => TimerSchedule.FromCronExpression("0 9 * * 1"),
            "monthly" => TimerSchedule.FromCronExpression("0 9 1 * *"),
            "immediate" => TimerSchedule.Immediate(),
            _ => TimerSchedule.FromDuration(TimeSpan.FromDays(30))
        });
    }
}
```

## Common Patterns

### Recurring Payments
```csharp
TimerSchedule.FromCronExpression("0 9 1 * *")
```

### Business Hours Processing
```csharp
TimerSchedule.FromCronExpression("0 9 * * 1-5")
```

### Priority-Based Execution
```csharp
return priority switch
{
    "urgent" => TimerSchedule.Immediate(),
    "high" => TimerSchedule.FromDuration(TimeSpan.FromMinutes(5)),
    "normal" => TimerSchedule.FromDuration(TimeSpan.FromHours(1)),
    _ => TimerSchedule.Daily
};
```

## Notes and Constraints

- `TimerSchedule.ToDaprJobSchedule()` maps schedules to Dapr job expressions.
- Immediate schedules are translated to `DateTime.UtcNow.AddSeconds(1)` for execution.
- Script evaluation uses the Result pattern; timer evaluation failures are logged by `ScriptTimerEvaluator`.

## Implementation References

- `src/BBT.Workflow.Domain/Definitions/Timer/TimerSchedule.cs`
- `src/BBT.Workflow.Domain/Tasks/Coordinator/ITaskTimerService.cs`
- `src/BBT.Workflow.Application/Tasks/Evaluators/ScriptTimerEvaluator.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/ScheduleTransitionsStep.cs`
- `src/BBT.Workflow.Application/BackgroundJobs/Handlers/TransitionTimerJobHandler.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Handlers/ScheduledTransitionHandler.cs`
