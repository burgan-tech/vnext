using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Unit tests for DynamicRoleGrant parsing.
/// </summary>
public sealed class DynamicRoleGrantTests
{
    #region TryParse - valid patterns

    [Fact]
    public void TryParse_UserQualifierWithSimplePath_ReturnsUserGrant()
    {
        var result = DynamicRoleGrant.TryParse("$user.$.context.Instance.Data.customer.ownerUserId");

        result.ShouldNotBeNull();
        result.Qualifier.ShouldBe(DynamicRoleQualifier.User);
        result.ContextPath.ShouldBe("$.context.Instance.Data.customer.ownerUserId");
    }

    [Fact]
    public void TryParse_UserQualifierWithArrayWildcard_ReturnsUserGrantWithIsArrayPath()
    {
        var result = DynamicRoleGrant.TryParse("$user.$.context.Instance.Data.assignedUsers[*].userId");

        result.ShouldNotBeNull();
        result.Qualifier.ShouldBe(DynamicRoleQualifier.User);
        result.ContextPath.ShouldBe("$.context.Instance.Data.assignedUsers[*].userId");
        result.IsArrayPath.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_UserBehalfOfQualifier_ReturnsUserBehalfOfGrant()
    {
        var result = DynamicRoleGrant.TryParse("$userBehalfOf.$.context.Instance.Data.customer.behalfOfUserId");

        result.ShouldNotBeNull();
        result.Qualifier.ShouldBe(DynamicRoleQualifier.UserBehalfOf);
        result.ContextPath.ShouldBe("$.context.Instance.Data.customer.behalfOfUserId");
    }

    [Fact]
    public void TryParse_RoleQualifier_ReturnsRoleGrant()
    {
        var result = DynamicRoleGrant.TryParse("$role.$.context.Instance.Data.permissions.requiredRole");

        result.ShouldNotBeNull();
        result.Qualifier.ShouldBe(DynamicRoleQualifier.Role);
        result.ContextPath.ShouldBe("$.context.Instance.Data.permissions.requiredRole");
    }

    [Fact]
    public void TryParse_RoleQualifierWithArrayWildcard_ReturnsRoleGrantWithIsArrayPath()
    {
        var result = DynamicRoleGrant.TryParse("$role.$.context.Instance.Data.approvers[*].role");

        result.ShouldNotBeNull();
        result.Qualifier.ShouldBe(DynamicRoleQualifier.Role);
        result.IsArrayPath.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_TransitionPath_ReturnsGrant()
    {
        var result = DynamicRoleGrant.TryParse("$role.$.context.Transition.Key");

        result.ShouldNotBeNull();
        result.Qualifier.ShouldBe(DynamicRoleQualifier.Role);
        result.ContextPath.ShouldBe("$.context.Transition.Key");
    }

    #endregion

    #region TryParse - prefix collision

    [Fact]
    public void TryParse_UserBehalfOfPrefix_NotMistakenForUser()
    {
        // $userBehalfOf must not be parsed as $user with remainder "BehalfOf.$.context..."
        var result = DynamicRoleGrant.TryParse("$userBehalfOf.$.context.Instance.Data.id");

        result.ShouldNotBeNull();
        result.Qualifier.ShouldBe(DynamicRoleQualifier.UserBehalfOf);
    }

    #endregion

    #region TryParse - null / invalid inputs

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("$InstanceStarter")]
    [InlineData("$PreviousUser")]
    [InlineData("morph-idm.maker")]
    [InlineData("$user.Instance.Data.field")]       // missing $.context.
    [InlineData("$role.context.Instance.Data.f")]   // missing $. prefix
    [InlineData("$user.$.context.")]                // empty path after prefix
    public void TryParse_InvalidOrNonDynamicInputs_ReturnsNull(string? input)
    {
        DynamicRoleGrant.TryParse(input).ShouldBeNull();
    }

    #endregion

    #region IsDynamicRole

    [Theory]
    [InlineData("$user.$.context.Instance.Data.id", true)]
    [InlineData("$userBehalfOf.$.context.Instance.Data.id", true)]
    [InlineData("$role.$.context.Instance.Data.id", true)]
    [InlineData("$InstanceStarter", false)]
    [InlineData("morph-idm.maker", false)]
    [InlineData(null, false)]
    public void IsDynamicRole_ReturnsExpected(string? role, bool expected)
    {
        DynamicRoleGrant.IsDynamicRole(role).ShouldBe(expected);
    }

    #endregion

    #region IsArrayPath

    [Fact]
    public void IsArrayPath_WhenPathContainsWildcard_ReturnsTrue()
    {
        var grant = DynamicRoleGrant.TryParse("$user.$.context.Instance.Data.items[*].id")!;
        grant.IsArrayPath.ShouldBeTrue();
    }

    [Fact]
    public void IsArrayPath_WhenPathHasNoWildcard_ReturnsFalse()
    {
        var grant = DynamicRoleGrant.TryParse("$user.$.context.Instance.Data.owner.id")!;
        grant.IsArrayPath.ShouldBeFalse();
    }

    #endregion
}
