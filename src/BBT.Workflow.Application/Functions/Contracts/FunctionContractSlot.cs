namespace BBT.Workflow.Functions.Contracts;

/// <summary>
/// Identifies which declared contract of a function is being resolved.
/// </summary>
public enum FunctionContractSlot
{
    /// <summary>The <c>sys-schemas</c> contract describing the request body.</summary>
    InputSchema = 0,

    /// <summary>The <c>sys-schemas</c> contract describing the response body.</summary>
    OutputSchema = 1,

    /// <summary>The <c>sys-views</c> contract the client renders to collect input.</summary>
    InputView = 2,

    /// <summary>The <c>sys-views</c> contract the client renders to present output.</summary>
    OutputView = 3
}
