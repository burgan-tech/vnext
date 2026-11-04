using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.LongPoll;

/// <summary>
/// A resume driven by the acknowledge fallback job must be told apart from one the client asked for:
/// the two share the pipeline but not the log line, so a flow whose client never acknowledges is
/// visible without reading episode spans.
/// </summary>
public class LongPollAckResumeServiceTriggerTests
{
    private sealed class CapturingLogger : ILogger<LongPollAckResumeService>
    {
        public List<(int EventId, LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    private static (LongPollAckResumeService Service, CapturingLogger Logger, IWorkflowExecutionService Execution)
        Build(bool awaitingAck = true)
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "k");
        if (awaitingAck)
            instance.ArmLongPollAck(Guid.NewGuid());

        var instances = Substitute.For<IInstanceRepository>();
        instances.FindAsync(Arg.Any<Expression<Func<Instance, bool>>>(), false, Arg.Any<CancellationToken>())
            .Returns(instance);

        var execution = Substitute.For<IWorkflowExecutionService>();
        execution.ExecuteTransitionAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = instance.Id, Status = InstanceStatus.Active }));

        var logger = new CapturingLogger();
        return (new LongPollAckResumeService(instances, execution, logger), logger, execution);
    }

    [Fact]
    public async Task Resume_FromTheFallbackJob_LogsTheFallbackEventNotTheAcknowledgeOne()
    {
        var (service, logger, _) = Build();

        using (WorkflowTraceLane.Reset(anchor: null))
        using (WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.AckTimeout, transitionKey: null))
        {
            var result = await service.ResumeAsync("core", "flow", "1.0.0", Guid.NewGuid());
            result.IsSuccess.ShouldBeTrue();
        }

        logger.Entries.ShouldContain(e => e.EventId == 20308 && e.Level == LogLevel.Information);
        logger.Entries.ShouldNotContain(e => e.EventId == 20301);
    }

    [Fact]
    public async Task Resume_FromAnAcknowledge_LogsTheAcknowledgeEvent()
    {
        var (service, logger, _) = Build();

        using (WorkflowTraceLane.Reset(anchor: null))
        {
            var result = await service.ResumeAsync("core", "flow", "1.0.0", Guid.NewGuid());
            result.IsSuccess.ShouldBeTrue();
        }

        logger.Entries.ShouldContain(e => e.EventId == 20301 && e.Level == LogLevel.Information);
        logger.Entries.ShouldNotContain(e => e.EventId == 20308);
    }

    [Fact]
    public async Task Resume_WhenNotAwaiting_RunsNoPipelineAndLogsNeitherResumeEvent()
    {
        var (service, logger, execution) = Build(awaitingAck: false);

        using (WorkflowTraceLane.Reset(anchor: null))
        using (WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.AckTimeout, transitionKey: null))
        {
            var result = await service.ResumeAsync("core", "flow", "1.0.0", Guid.NewGuid());
            result.IsSuccess.ShouldBeTrue();
        }

        await execution.DidNotReceiveWithAnyArgs().ExecuteTransitionAsync(default!, default);
        logger.Entries.ShouldNotContain(e => e.EventId == 20301 || e.EventId == 20308);
    }
}
