using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionAccessPolicy"/> - the single gate both function execution and
/// function discovery pass through, so a caller denied on one is denied on the other. The gate covers
/// <c>scope</c> only; <c>function.roles</c> is evaluated by the <c>authorize</c> function, not here.
/// </summary>
public sealed class FunctionAccessPolicyTests
{
    private const string TestDomain = FunctionTestFactory.Domain;
    private const string TestVersion = FunctionTestFactory.Version;
    private const string FunctionKey = "my-fn";
    private const string TestFlow = "my-flow";

    private readonly FunctionAccessPolicy _policy = new();

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

    /// <summary>
    /// The behaviour change: custom function invocation is no longer role-gated by the runtime.
    /// A function declaring an allowlist grant still runs for a caller carrying no roles at all —
    /// the middle tier owns that decision, and <c>authorize</c> remains the surface that reports it.
    /// </summary>
    [Fact]
    public async Task RolesDeclared_AreNotEnforced_AndTheCallIsAllowed()
    {
        var result = await Authorize(FunctionWithRoles(), instance: null, workflow: null);

        result.IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// The scope gate outranks the (now absent) role gate: a roles-bearing Flow-scoped function
    /// called without an instance is still rejected on scope.
    /// </summary>
    [Fact]
    public async Task RolesDeclared_StillFailsTheScopeGate()
    {
        var result = await Authorize(FunctionWithRoles("F"), instance: null, workflow: null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionScopeNotSatisfied);
    }

    private Task<BBT.Aether.Results.Result> Authorize(
        Function function, Instance? instance, Definitions.Workflow? workflow) =>
        _policy.AuthorizeAsync(function, instance, workflow, null, null);

    private static Function Function(string scope) =>
        FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(scope: scope), FunctionKey);

    private static Function FunctionWithRoles(string scope = "D") =>
        FunctionTestFactory.FromJson(
            FunctionTestFactory.Attributes("""
                "roles": [ { "role": "backoffice.operator", "grant": "allow" } ]
                """, scope: scope),
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
