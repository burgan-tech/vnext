namespace BBT.Workflow.Execution;

/// <summary>
/// The pipeline's decision, taken at settlement, that the current activation episode has reached
/// its rest point — recorded on <see cref="PipelineDirectives"/> and turned into the
/// <c>Instance.Activation/{key}</c> span by the runner <em>after</em> the unit of work commits, because
/// the settlement's Busy→Active write is not observable by a client until then.
/// </summary>
/// <param name="Outcome">The rest point reached; one of <see cref="Logging.TelemetryConstants.ActivationOutcomes"/>.</param>
/// <param name="CasFlipped">True when this settlement's compare-and-set made the instance Active.</param>
/// <param name="StateTo">The state the instance rests in.</param>
public sealed record ActivationVerdict(string Outcome, bool CasFlipped, string? StateTo);
