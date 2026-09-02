namespace BBT.Workflow.Workers.Inbox.Tracing;

/// <summary>How a consumed event's handler span relates to the producer's trace.</summary>
public enum EventTraceMode
{
    /// <summary>Immediate async COMMAND: the consumer continues the producer's trace
    /// (parent = the event's TraceParent). Same policy as before this change.</summary>
    ContinueTrace,

    /// <summary>FACT delivery: the handler roots its own delivery trace; the producer's
    /// TraceParent becomes an ActivityLink. Lane Reset side-effects are identical in both
    /// modes — a genuine backup-settled resume still anchors into the parent's tree.</summary>
    LinkedDelivery
}
