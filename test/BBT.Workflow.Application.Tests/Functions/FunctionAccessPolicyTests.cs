using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionAccessPolicy"/> - the single gate both function execution and
/// function discovery pass through, so a caller denied on one is denied on the other.
/// </summary>
public sealed class FunctionAccessPolicyTests
{
    private const string TestDomain = FunctionTestFactory.Domain;
    private const string TestVersion = FunctionTestFactory.Version;
    private const string FunctionKey = "my-fn";
    private const string TestFlow = "my-flow";

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ITransitionAuthorizationManager _authorizationManager =
        Substitute.For<ITransitionAuthorizationManager>();
    private readonly FunctionAccessPolicy _policy;

    public FunctionAccessPolicyTests()
    {
        _policy = new FunctionAccessPolicy(_currentUser, _authorizationManager);
    }

    [Fact]
    public async Task DomainScope_WithoutAnInstance_IsAllowed()
    {
        var result = await Authorize(Function("D"), instance: null, workflow: null);

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("F")]
    [InlineData("I")]
    public async Task NonDomainScope_WithoutAnInstance_IsForbidden(string scope)
    {
        var result = await Authorize(Function(scope), instance: null, workflow: null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionScopeNotSatisfied);
    }

    [Fact]
    public async Task FlowScope_RequiresTheFunctionToBeDeclaredInTheFlow()
    {
        var result = await Authorize(Function("F"), Instance(), Workflow(declareFunction: false));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionScopeNotSatisfied);
    }

    [Fact]
    public async Task FlowScope_DeclaredInTheFlow_IsAllowed()
    {
        var result = await Authorize(Function("F"), Instance(), Workflow(declareFunction: true));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InstanceScope_WithAnInstance_NeedsNoFlowDeclaration()
    {
        var result = await Authorize(Function("I"), Instance(), Workflow(declareFunction: false));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task NoRolesDeclared_SkipsRoleEvaluationEntirely()
    {
        await Authorize(Function("D"), instance: null, workflow: null);

        await _authorizationManager.DidNotReceiveWithAnyArgs()
            .IsAnyRoleAllowedForGrantsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task RolesDeclared_AndDenied_IsForbidden()
    {
        _authorizationManager
            .IsAnyRoleAllowedForGrantsAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await Authorize(FunctionWithRoles(), instance: null, workflow: null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task RolesDeclared_AndAllowed_IsAllowed()
    {
        _authorizationManager
            .IsAnyRoleAllowedForGrantsAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await Authorize(FunctionWithRoles(), instance: null, workflow: null);

        result.IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// Dynamic role grants navigate <c>$.context.Headers</c> / <c>QueryParameters</c>; a surface that
    /// omits the request context makes those grants silently unable to match.
    /// </summary>
    [Fact]
    public async Task RequestContext_IsPassedToTheEvaluator()
    {
        _authorizationManager
            .IsAnyRoleAllowedForGrantsAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var headers = new Dictionary<string, string?> { ["x-user"] = "alice" };
        var query = new Dictionary<string, string?> { ["scope"] = "wide" };

        await _policy.AuthorizeAsync(FunctionWithRoles(), null, null, headers, query);

        await _authorizationManager.Received(1).IsAnyRoleAllowedForGrantsAsync(
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyCollection<RoleGrant>>(),
            Arg.Any<Instance?>(),
            Arg.Is<AuthorizationRequestContext?>(c => c != null),
            Arg.Any<CancellationToken>());
    }

    private Task<BBT.Aether.Results.Result> Authorize(
        Function function, Instance? instance, Definitions.Workflow? workflow) =>
        _policy.AuthorizeAsync(function, instance, workflow, null, null);

    private static Function Function(string scope) =>
        FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(scope: scope), FunctionKey);

    private static Function FunctionWithRoles() =>
        FunctionTestFactory.FromJson(
            FunctionTestFactory.Attributes("""
                "roles": [ { "role": "backoffice.operator", "grant": "allow" } ]
                """),
            FunctionKey);

    private static Instance Instance() =>
        Instances.Instance.Create(System.Guid.NewGuid(), TestFlow, TestVersion, "test-key");

    private static Definitions.Workflow Workflow(bool declareFunction)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestFlow, TestDomain, "sys-flows", TestVersion));
        workflow.SetType("F");
        if (declareFunction)
            workflow.AddFunction(new Reference(FunctionKey, TestDomain, "sys-functions", TestVersion));
        return workflow;
    }
}
