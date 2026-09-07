using System.Diagnostics;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Extentions;

/// <summary>
/// Spans for instance-data extension enrichment (<see cref="InstanceExtensionService"/>), which
/// previously produced no spans at all: extension-ref resolution and the enrichment envelope were
/// invisible, leaving cache reads like <c>sys-extensions</c> orphaned on the root transaction.
/// </summary>
public static class ExtensionActivityHelper
{
    /// <summary>Source name as a const so test listeners never touch the static field (type-init trap).</summary>
    public const string SourceName = TelemetryConstants.ActivitySources.Extensions;

    /// <summary>ActivitySource for extension spans. Registered in Telemetry:Tracing:AdditionalSources.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Tag: how many extension references a resolve covered.</summary>
    public const string TagRefCount = "vnext.extension.ref.count";

    /// <summary>Starts the envelope span for one enrichment pass, named <c>Extension.Process/{scope}</c>.</summary>
    public static Activity? StartProcess(string workflowKey, ExtensionScope scope)
    {
        var activity = ActivitySource.StartActivity(
            $"Extension.Process/{scope}", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TelemetryConstants.TagNames.Flow, workflowKey);
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }
        return activity;
    }

    /// <summary>Starts the span covering extension component-ref resolution (parallel cache fetches).</summary>
    public static Activity? StartResolve(int referenceCount)
    {
        var activity = ActivitySource.StartActivity("Extension.Resolve", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TagRefCount, referenceCount);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }
        return activity;
    }
}
