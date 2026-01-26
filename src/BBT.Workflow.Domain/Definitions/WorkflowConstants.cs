namespace BBT.Workflow.Definitions;

public class WorkflowConstants
{
    public const string DefaultVersion = "1.0.0";

    public const int MaxKeyLength = 100;
    public const int MaxTypeLength = 3;
    public const int MaxVersionLength = 180;
    public const int MaxDomainLength = 50;
    public const int MaxFlowLength = 100;
    public const int MaxDurationLength = 100;
    public const int MaxETagLength = 26;
    public const int MaxDataHashLength = 40;
    public const int MaxTimerResetLength = 26;
}

public class LanguageLabelConstants
{
    public const int MaxLabelLength = 180;
    public const int MaxLanguageLength = 7;
}

public class ViewConstants
{
    public const int MaxKeyLength = WorkflowConstants.MaxKeyLength;
}

public class TaskConstants
{
    public const int MaxKeyLength = WorkflowConstants.MaxKeyLength;
}

public class StateConstants
{
    public const int MaxKeyLength = WorkflowConstants.MaxKeyLength;
}

public class TransitionConstants
{
    public const int MaxKeyLength = WorkflowConstants.MaxKeyLength;
    public const int MaxTargetLength = StateConstants.MaxKeyLength;
    public const int MaxVersionStrategyLength = 10;
}

public class FunctionConstants
{
    public const int MaxKeyLength = WorkflowConstants.MaxKeyLength;
}

public class SchemaConstants
{
    public const int MaxKeyLength = WorkflowConstants.MaxKeyLength;
}