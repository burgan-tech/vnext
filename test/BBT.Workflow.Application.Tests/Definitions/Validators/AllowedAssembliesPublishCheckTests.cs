using System.Linq;
using System.Text.Json;
using BBT.Workflow.Scripting.Sandbox;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

public class AllowedAssembliesPublishCheckTests
{
    private static IScriptAssemblyCatalog Catalog(params string[] available)
    {
        var mock = new Mock<IScriptAssemblyCatalog>();
        mock.Setup(c => c.IsAvailable(It.IsAny<string>()))
            .Returns<string>(n => available.Contains(n, System.StringComparer.OrdinalIgnoreCase));
        return mock.Object;
    }

    private static ComponentValidationResult Run(string type, string json, params string[] available)
    {
        var result = new ComponentValidationResult();
        AllowedAssembliesPublishCheck.Validate(type, JsonDocument.Parse(json).RootElement, Catalog(available), result);
        return result;
    }

    [Fact]
    public void FlowLevelScripts_UnavailableAssembly_ReportsError()
    {
        var result = Run("sys-flows",
            """{ "scripts": { "allowedAssemblies": ["System.Linq", "Acme.Missing"] } }""",
            "System.Linq");

        result.ValidationErrors.Count.ShouldBe(1);
        var error = result.ValidationErrors.Single();
        error.MemberNames.ShouldContain("sys-flows.scripts.allowedAssemblies[1]");
        error.ErrorMessage!.ShouldContain("'Acme.Missing'");
    }

    [Fact]
    public void NestedSlotScripts_UnavailableAssembly_ReportsPathToSlot()
    {
        var result = Run("sys-flows",
            """
            { "states": [ { "onEntries": [ {}, { "mapping": { "location": "./a.csx", "code": "eA==",
              "scripts": { "allowedAssemblies": ["Acme.Missing"] } } } ] } ] }
            """);

        result.ValidationErrors.Single().MemberNames
            .ShouldContain("sys-flows.states[0].onEntries[1].mapping.scripts.allowedAssemblies[0]");
    }

    [Fact]
    public void EveryUnavailableName_GetsItsOwnError()
    {
        var result = Run("sys-functions",
            """{ "output": { "scripts": { "allowedAssemblies": ["A.Missing", "B.Missing"] } } }""");

        result.ValidationErrors.Count.ShouldBe(2);
    }

    [Fact]
    public void AllNamesAvailable_NoError()
    {
        Run("sys-extensions",
            """{ "task": { "mapping": { "scripts": { "allowedAssemblies": ["System.Linq"] } } } }""",
            "System.Linq").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void NoScriptsBlock_NoError()
    {
        Run("sys-tasks", """{ "type": "6", "config": {} }""").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ScriptsLookAlikeThatIsNotAStringArray_IsIgnored()
    {
        // e.g. a JSON-Schema-shaped object: allowedAssemblies is an object, not a string array.
        Run("sys-flows",
            """{ "scripts": { "allowedAssemblies": { "type": "array" } } }""").IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("sys-flows", true)]
    [InlineData("sys-tasks", true)]
    [InlineData("sys-functions", true)]
    [InlineData("sys-extensions", true)]
    [InlineData("sys-schemas", false)]
    [InlineData("sys-views", false)]
    [InlineData("sys-mappings", false)]
    public void AppliesTo_OnlyScriptCarryingComponentTypes(string type, bool expected)
    {
        AllowedAssembliesPublishCheck.AppliesTo(type).ShouldBe(expected);
    }
}
