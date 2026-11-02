using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Logging;

public class WorkflowErrorsVersionPolicyTests
{
    [Fact]
    public void FlowVersionNotActivated_IsNotFound_WithCodeAndTarget()
    {
        var error = WorkflowErrors.FlowVersionNotActivated("core", "account-opening", "2.2.0", "2.1");

        error.Code.ShouldBe("Instance:100051");
        error.Code.ShouldBe(WorkflowErrorCodes.FlowVersionNotActivated);
        error.Prefix.ShouldBe(Error.NotFound("x", "y").Prefix);
        error.Target.ShouldBe("core/account-opening@2.2.0");
        error.Message.ShouldContain("published but not activated");
    }

    [Fact]
    public void FlowDefaultVersionInvalid_IsConflict_WithCodeAndTarget()
    {
        var error = WorkflowErrors.FlowDefaultVersionInvalid("core", "account-opening", "9.9");

        error.Code.ShouldBe("Instance:100052");
        error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
        error.Prefix.ShouldBe(Error.Conflict("x", "y").Prefix);
        error.Prefix.ShouldNotBe(Error.NotFound("x", "y").Prefix);
        error.Target.ShouldBe("core/account-opening");
        error.Message.ShouldContain("9.9");
    }
}
