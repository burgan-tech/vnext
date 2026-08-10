using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

public class InstanceConstants
{
    public const int MaxKeyLength = WorkflowConstants.MaxKeyLength;
    public const int MaxStatusLength = 3;
    public const int MaxStageLength = 120;
    public const int TransitionLockExpiryInSeconds = 300; // 5 minutes
}

public class InstanceActionConstants
{
    public const int MaxStatusLength = 70;
}

public class InstanceJobConstants
{
    public const int MaxJobNameLength = 500;
    public const int MaxIdempotencyKeyLength = 128;
    public const int MaxRequestFingerprintLength = 64;
    public const int MaxErrorCodeLength = 200;
}
