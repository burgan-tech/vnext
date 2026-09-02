using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Functions;

/// <summary>
/// Spans for the function-execution path (<see cref="FunctionAppService.ExecuteFunctionAsync"/>),
/// which previously produced no phase spans of its own — authorization, request validation,
/// cache-key/generation resolution and response building were all unattributable inside the
/// endpoint transaction. Envelope + per-phase children, always-on Business category.
/// </summary>
public static class FunctionActivityHelper
{
    /// <summary>Source name as a const so test listeners never touch the static field (type-init trap).</summary>
    public const string SourceName = "BBT.Workflow.Functions";

    /// <summary>ActivitySource for function-path spans. Registered in Telemetry:Tracing:AdditionalSources.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Operation name for the authorization phase (function access policy).</summary>
    public const string OperationAuthorize = "Function.Authorize";

    /// <summary>Operation name for verb + input-schema validation (may run schema rule scripts).</summary>
    public const string OperationValidateRequest = "Function.ValidateRequest";

    /// <summary>Operation name for response building (representation / IOutputHandler script).</summary>
    public const string OperationBuildResponse = "Function.BuildResponse";

    /// <summary>Starts the envelope span for one function execution, named <c>Function.Execute/{key}</c>.</summary>
    public static Activity? StartExecute(string functionKey)
    {
        var activity = ActivitySource.StartActivity(
            $"Function.Execute/{functionKey}", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }
        return activity;
    }

    /// <summary>Starts one phase child (see the Operation* consts).</summary>
    public static Activity? StartPhase(string operationName)
    {
        var activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }
        return activity;
    }
}
