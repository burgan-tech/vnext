using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Unit tests for PredefinedInstanceRoles.
/// </summary>
public class PredefinedInstanceRolesTests
{
    [Fact]
    public void InstanceStarter_ShouldBeDollarInstanceStarter()
    {
        Assert.Equal("$InstanceStarter", PredefinedInstanceRoles.InstanceStarter);
    }

    [Fact]
    public void PreviousUser_ShouldBeDollarPreviousUser()
    {
        Assert.Equal("$PreviousUser", PredefinedInstanceRoles.PreviousUser);
    }

    [Fact]
    public void InstanceBehalfOfStarter_ShouldBeDollarInstanceBehalfOfStarter()
    {
        Assert.Equal("$InstanceBehalfOfStarter", PredefinedInstanceRoles.InstanceBehalfOfStarter);
    }

    [Fact]
    public void PreviousBehalfOfUser_ShouldBeDollarPreviousBehalfOfUser()
    {
        Assert.Equal("$PreviousBehalfOfUser", PredefinedInstanceRoles.PreviousBehalfOfUser);
    }

    [Theory]
    [InlineData("$InstanceStarter", true)]
    [InlineData("$PreviousUser", true)]
    [InlineData("$InstanceBehalfOfStarter", true)]
    [InlineData("$PreviousBehalfOfUser", true)]
    [InlineData("$instancestarter", false)]
    [InlineData("$previoususer", false)]
    [InlineData("$instancebehalfofstarter", false)]
    [InlineData("$previousbehalfofuser", false)]
    [InlineData("role.maker", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("  $InstanceStarter  ", true)]
    [InlineData("  $InstanceBehalfOfStarter  ", true)]
    public void IsPredefinedRole_ShouldReturnExpected(string? role, bool expected)
    {
        Assert.Equal(expected, PredefinedInstanceRoles.IsPredefinedRole(role));
    }
}
