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

    #region Classify

    [Theory]
    [InlineData("$user.$.context.Instance.Data.ownerId")]
    [InlineData("$userBehalfOf.$.context.Instance.Data.behalfOfId")]
    [InlineData("$role.$.context.Transition.Key")]
    [InlineData("$user.$.context.Instance.Data.assignedUsers[*].userId")]
    public void Classify_WhenWellFormedDynamicRole_ReturnsWellFormed(string role)
    {
        DynamicRoleGrant.Classify(role).ShouldBe(DynamicRoleFormat.WellFormed);
    }

    [Theory]
    // Static role names and the four predefined instance roles carry nothing to validate.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("backoffice.operator")]
    [InlineData("$InstanceStarter")]
    [InlineData("$PreviousUser")]
    [InlineData("$InstanceBehalfOfStarter")]
    [InlineData("$PreviousBehalfOfUser")]
    // No trailing dot: not a qualifier prefix, so this is a static role name to the runtime too.
    [InlineData("$user")]
    [InlineData("$role")]
    public void Classify_WhenNotDynamicRole_ReturnsNotDynamic(string? role)
    {
        DynamicRoleGrant.Classify(role).ShouldBe(DynamicRoleFormat.NotDynamic);
    }

    [Theory]
    [InlineData("$user.customer")]
    [InlineData("$role.someRole")]
    [InlineData("$userBehalfOf.")]
    [InlineData("$user.context.Instance.Data.x")]
    // Case variants: TryParse compares the "$.context." literal with Ordinal, so the runtime would
    // treat these as static role names that can never match. Validation must reject them, not accept
    // them with a looser OrdinalIgnoreCase comparison.
    [InlineData("$user.$.CONTEXT.Instance.Data.x")]
    [InlineData("$user.$.Context.Instance.Data.x")]
    public void Classify_WhenQualifierPresentButContextPrefixMissing_ReturnsMissingContextPrefix(string role)
    {
        DynamicRoleGrant.Classify(role).ShouldBe(DynamicRoleFormat.MissingContextPrefix);
    }

    [Theory]
    [InlineData("$user.$.context.")]
    [InlineData("$role.$.context.   ")]
    [InlineData("$userBehalfOf.$.context.")]
    public void Classify_WhenNavigationPathEmpty_ReturnsEmptyNavigationPath(string role)
    {
        DynamicRoleGrant.Classify(role).ShouldBe(DynamicRoleFormat.EmptyNavigationPath);
    }

    [Fact]
    public void Classify_ChecksUserBehalfOfBeforeUser()
    {
        // "$userBehalfOf." also starts with "$user", so a naive order would strip the wrong prefix
        // and leave "BehalfOf.$.context.A", which does not open with "$.context.".
        DynamicRoleGrant.Classify("$userBehalfOf.$.context.A").ShouldBe(DynamicRoleFormat.WellFormed);
    }

    /// <summary>
    /// The invariant that keeps definition-time validation honest: Classify reports WellFormed for
    /// exactly the inputs TryParse accepts. If this ever fails, the validator is either rejecting
    /// grants the runtime honors or accepting grants the runtime silently ignores.
    /// </summary>
    [Theory]
    [InlineData("backoffice.operator")]
    [InlineData("$InstanceStarter")]
    [InlineData("$user")]
    [InlineData("$user.customer")]
    [InlineData("$user.$.context.")]
    [InlineData("$user.$.Context.Instance.Data.x")]
    [InlineData("$user.$.context.Instance.Data.x")]
    [InlineData("$userBehalfOf.$.context.Instance.Data.x")]
    [InlineData("$role.$.context.Transition.Key")]
    [InlineData("$userBehalfOf.")]
    public void Classify_WellFormed_MatchesTryParseExactly(string role)
    {
        var wellFormed = DynamicRoleGrant.Classify(role) == DynamicRoleFormat.WellFormed;

        wellFormed.ShouldBe(DynamicRoleGrant.TryParse(role) != null);
        wellFormed.ShouldBe(DynamicRoleGrant.IsDynamicRole(role));
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
