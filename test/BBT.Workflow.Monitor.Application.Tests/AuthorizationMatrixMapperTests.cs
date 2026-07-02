using BBT.Workflow.Monitor.Authorization;
using BBT.Workflow.Monitor.Authorization.DTOs;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests;

public class AuthorizationMatrixMapperTests
{
    [Fact]
    public void IsAllowed_DenyOverridesAllow()
    {
        var grants = new List<MonitorRoleGrant>
        {
            new() { Role = "maker", Grant = "allow" },
            new() { Role = "maker", Grant = "deny" }
        };
        Assert.False(AuthorizationMatrixMapper.IsAllowed(grants, new[] { "maker" }));
    }

    [Fact]
    public void IsAllowed_AllowWhenRoleMatchesAndNoDeny()
    {
        var grants = new List<MonitorRoleGrant> { new() { Role = "checker", Grant = "allow" } };
        Assert.True(AuthorizationMatrixMapper.IsAllowed(grants, new[] { "checker" }));
    }

    [Fact]
    public void IsAllowed_DefaultDenyWhenNoMatch()
    {
        var grants = new List<MonitorRoleGrant> { new() { Role = "checker", Grant = "allow" } };
        Assert.False(AuthorizationMatrixMapper.IsAllowed(grants, new[] { "maker" }));
    }
}
