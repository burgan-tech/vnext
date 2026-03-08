using System;
using BBT.Workflow.Instances;

namespace BBT.Workflow;

public static class InstanceFactory
{
    public static Instance CreateDefault(string? key = "test-key")
    {
        return Instance.Create(Guid.NewGuid(), "sys-flows", "1.0.0",key);
    }
}