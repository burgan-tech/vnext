using System;
using System.Collections.Generic;
using BBT.Aether.Users;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

public class CallerCredentialScopeTests
{
    [Fact]
    public void Enter_RunsAsTheHeaderSet_AndRestoresOnDispose()
    {
        var currentUser = new BBT.Aether.Users.CurrentUser(AsyncLocalCurrentUserAccessor.Instance);
        using var ambient = currentUser.ChangeFromHeaders(
            new Dictionary<string, string?> { ["sub"] = "ambient", ["act_sub"] = "ambient-actor" });
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sub"] = "", ["act_sub"] = "a1", ["x-parent-instance-id"] = "p"
        };

        using (CallerCredentialScope.Enter(currentUser, headers))
        {
            currentUser.UserName.ShouldBeNull();
            currentUser.ActorUserName.ShouldBe("a1");
            headers.ContainsKey("sub").ShouldBeFalse();
        }

        currentUser.UserName.ShouldBe("ambient");
    }

    [Fact]
    public void Enter_WithNoHeaders_IsAnAnonymousUser_NotTheAmbientOne()
    {
        var currentUser = new BBT.Aether.Users.CurrentUser(AsyncLocalCurrentUserAccessor.Instance);
        using var ambient = currentUser.ChangeFromHeaders(new Dictionary<string, string?> { ["sub"] = "ambient" });

        using (CallerCredentialScope.Enter(currentUser, new Dictionary<string, string?>()))
            currentUser.UserName.ShouldBeNull();
    }
}
