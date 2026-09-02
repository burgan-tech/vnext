using System;
using System.Diagnostics;
using OpenTelemetry;

namespace BBT.Workflow.HttpApi.Shared.Telemetry;

/// <summary>
/// Drops the root <c>Db.*</c> spans the Outbox/Inbox worker poll loops mint on every idle cycle.
/// A poll that finds nothing produces one parentless EF span, which the backend stores as a
/// complete one-span trace — measured at roughly 13 root traces per minute per worker, purely
/// from idling. Real work is unaffected: after the outbox processor roots its own
/// <c>Outbox.Process</c> episode, every database command that belongs to actual processing runs
/// UNDER a span, so a parentless <c>Db.*</c> span in these hosts is idle noise by construction.
/// <para>
/// Clearing <see cref="ActivityTraceFlags.Recorded"/> is the same export-drop technique
/// <c>PipelineStepActivityHelper.SetStepOutcome</c> uses for no-work pipeline steps: exporters
/// skip the span, while it stays valid in-process so a child started inside it would still parent
/// correctly (there is none here, by definition — a root span has no parent).
/// </para>
/// <para>
/// Registered only where <c>Telemetry:Tracing:DropRootDbSpans</c> is <c>true</c> — the two worker
/// hosts (Inbox/Outbox). Orchestration and Execution have no idle poll loop and are left alone.
/// </para>
/// </summary>
public sealed class IdlePollSpanProcessor : BaseProcessor<Activity>
{
    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        if (activity is null)
        {
            return;
        }

        var isRoot = activity.Parent is null && activity.ParentSpanId == default;
        if (isRoot && activity.DisplayName.StartsWith("Db.", StringComparison.Ordinal))
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
