namespace BBT.Workflow.Workers.Inbox.Tracing;

/// <summary>How a consumed event's handler span relates to the producer's trace.</summary>
public enum EventTraceMode
{
    /// <summary>Immediate async COMMAND: the consumer continues the producer's trace
    /// (parent = the event's TraceParent). Same policy as before this change.</summary>
    ContinueTrace,

    /// <summary>FACT delivery: the handler roots its own delivery trace without cross-trace
    /// ActivityLinks. Producer and transport ids remain searchable tags, preventing Elastic from
    /// splicing delayed backup delivery into the business waterfall. Lane Reset side-effects are
    /// identical in both modes — a genuine backup-settled resume still anchors into the parent's
    /// tree.</summary>
    IsolatedDelivery
}
