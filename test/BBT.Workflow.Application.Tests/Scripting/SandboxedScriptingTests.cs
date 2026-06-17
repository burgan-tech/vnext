using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Helpers;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Public test contract referenced by sandboxed scripts (passed as an explicit metadata reference,
/// independent of the assembly allow-list).
/// </summary>
public interface ISandboxTestCalc
{
    int Calc();
}

/// <summary>
/// Unit tests for the sandboxed compile path (<see cref="CSharpEvaluator"/> + <see cref="BannedApiAnalyzer"/>)
/// and the helper registry (<see cref="ScriptHelperRegistry"/>). These exercise the core acceptance
/// criteria without any database or DI container.
/// </summary>
public class SandboxedScriptingTests
{
    private static ScriptSandboxOptions EnabledSandbox() => new()
    {
        Enabled = true,
        AllowUnsafe = false,
        AllowedAssemblies =
        {
            "System.Private.CoreLib",
            "System.Runtime",
            "System.Collections",
            "System.Linq",
            "netstandard"
        },
        BannedNamespaces = [] // mandatory baseline still applies
    };

    private static MetadataReference ContractRef =>
        MetadataReference.CreateFromFile(typeof(ISandboxTestCalc).Assembly.Location);

    [Fact]
    public async Task Sandbox_Blocks_Banned_Namespace()
    {
        var evaluator = new CSharpEvaluator(EnabledSandbox());
        const string code = "public class C : ISandboxTestCalc { public int Calc() { var s = new System.IO.MemoryStream(); return (int)s.Length; } }";

        var ex = await Should.ThrowAsync<ScriptCompilationException>(async () =>
            await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(code, extraReferences: [ContractRef]));

        ex.Message.ShouldContain("System.IO");
    }

    [Fact]
    public async Task MandatoryBan_Cannot_Be_Removed_By_Config()
    {
        // Even with an empty configured ban list, the mandatory baseline still blocks System.IO.
        var options = EnabledSandbox();
        options.BannedNamespaces = [];
        var evaluator = new CSharpEvaluator(options);
        const string code = "public class C : ISandboxTestCalc { public int Calc() { var s = new System.IO.MemoryStream(); return (int)s.Length; } }";

        await Should.ThrowAsync<ScriptCompilationException>(async () =>
            await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(code, extraReferences: [ContractRef]));
    }

    [Fact]
    public async Task Sandbox_Blocks_Net_Http_Even_When_Assembly_Granted()
    {
        // Granting the assembly widens the reference allow-list but must NOT bypass the mandatory
        // banned-namespace analyzer: System.Net / System.Net.Http stay blocked.
        var options = EnabledSandbox();
        options.AllowedAssemblies.Add("System.Net.Http");
        var evaluator = new CSharpEvaluator(options);

        const string code =
            "public class C : ISandboxTestCalc { public int Calc() { var h = new System.Net.Http.HttpClient(); return 0; } }";

        var ex = await Should.ThrowAsync<ScriptCompilationException>(async () =>
            await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
                code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]));

        ex.Message.ShouldContain("System.Net");
    }

    [Fact]
    public async Task Sandbox_Blocks_DllImport()
    {
        var evaluator = new CSharpEvaluator(EnabledSandbox());
        const string code =
            "public class C : ISandboxTestCalc { [System.Runtime.InteropServices.DllImport(\"x\")] static extern int Native(); public int Calc() => 1; }";

        var ex = await Should.ThrowAsync<ScriptCompilationException>(async () =>
            await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(code, extraReferences: [ContractRef]));

        ex.Message.ShouldContain("DllImport");
    }

    [Fact]
    public async Task Sandbox_Allows_Threading_And_Tasks()
    {
        // System.Threading (and System.Threading.Tasks) are intentionally allowed under the sandbox.
        var evaluator = new CSharpEvaluator(EnabledSandbox());
        const string code =
            "public class C : ISandboxTestCalc { public int Calc() { int n = 6; System.Threading.Interlocked.Increment(ref n); var t = System.Threading.Tasks.Task.FromResult(n); return t.Result; } }";

        var instance = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]);

        instance.Calc().ShouldBe(7);
    }

    [Fact]
    public async Task Sandbox_Disabled_Still_Blocks_Mandatory_Namespace()
    {
        // Even with the sandbox disabled, the mandatory bans are always enforced so mapping code can
        // never use IO/network/reflection/etc.
        var evaluator = new CSharpEvaluator(new ScriptSandboxOptions { Enabled = false });
        const string code = "public class C : ISandboxTestCalc { public int Calc() { var s = new System.IO.MemoryStream(); return 42; } }";

        var ex = await Should.ThrowAsync<ScriptCompilationException>(async () =>
            await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
                code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]));

        ex.Message.ShouldContain("System.IO");
    }

    [Fact]
    public async Task Sandbox_Disabled_Compiles_Benign_Mapping()
    {
        // A mapping that touches no banned namespace compiles normally when the sandbox is disabled.
        var evaluator = new CSharpEvaluator(new ScriptSandboxOptions { Enabled = false });
        const string code = "public class C : ISandboxTestCalc { public int Calc() { var list = new System.Collections.Generic.List<int> { 21 }; return list[0] * 2; } }";

        var instance = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]);

        instance.Calc().ShouldBe(42);
    }

    [Fact]
    public async Task Sandbox_Allows_Dynamic_And_ExpandoObject_When_Expressions_Referenced()
    {
        // Regression: `dynamic` needs DynamicAttribute and System.Dynamic.ExpandoObject (both in
        // System.Linq.Expressions) + the Microsoft.CSharp runtime binder. These are runtime-owned
        // references that must always be present even under the sandbox.
        var evaluator = new CSharpEvaluator(EnabledSandbox());
        const string code =
            "public class C : ISandboxTestCalc { public int Calc() { dynamic o = new System.Dynamic.ExpandoObject(); o.x = 5; return (int)o.x; } }";

        var refs = new[]
        {
            ContractRef,
            MetadataReference.CreateFromFile(typeof(System.Dynamic.ExpandoObject).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo).Assembly.Location)
        };

        var instance = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: refs, usingDirectives: ["BBT.Workflow.Scripting"]);

        instance.Calc().ShouldBe(5);
    }

    private static MetadataReference SoapTaskRef =>
        MetadataReference.CreateFromFile(typeof(BBT.Workflow.Definitions.SoapTask).Assembly.Location);

    private static MetadataReference XmlImplRef =>
        MetadataReference.CreateFromFile(typeof(System.Xml.XmlDocument).Assembly.Location);

    /// <summary>Resolves a platform facade assembly by simple name from the trusted-platform-assembly set.</summary>
    private static MetadataReference ResolveFacade(string simpleName)
    {
        var tpa = (System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, System.StringSplitOptions.RemoveEmptyEntries);
        var path = System.Array.Find(tpa, p =>
            string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), simpleName, System.StringComparison.OrdinalIgnoreCase));
        path.ShouldNotBeNull($"Facade '{simpleName}' must be present in the TPA set on this runtime.");
        return MetadataReference.CreateFromFile(path);
    }

    private const string SoapTouchingCode =
        "public class C : ISandboxTestCalc { public int Calc() { " +
        "var t = BBT.Workflow.Definitions.SoapTask.CreateEmpty(); t.SetBody(\"<a/>\"); return t.Body!.Length; } }";

    [Fact]
    public async Task Sandbox_Compiles_SoapTask_Mapping_With_Xml_Facade()
    {
        // Mirror the engine's reference contract under the sandbox: the System.Xml.ReaderWriter facade
        // (XmlDocument's metadata identity) plus the System.Private.Xml implementation. With both,
        // a mapping touching SoapTask compiles even when the sandbox is enabled.
        var evaluator = new CSharpEvaluator(EnabledSandbox());

        var instance = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            SoapTouchingCode,
            extraReferences: [ContractRef, SoapTaskRef, XmlImplRef, ResolveFacade("System.Xml.ReaderWriter")],
            usingDirectives: ["BBT.Workflow.Scripting"]);

        instance.Calc().ShouldBe(4);
    }

    [Fact]
    public async Task Sandbox_Disabled_Compiles_SoapTask_Mapping_With_Xml_Facade()
    {
        // The reported failure (CS0012 / System.Xml.ReaderWriter) is on the Execution host where the
        // sandbox is disabled. Supplying the facade + implementation references compiles a SoapTask
        // mapping on that path too.
        var evaluator = new CSharpEvaluator(new ScriptSandboxOptions { Enabled = false });

        var instance = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            SoapTouchingCode,
            extraReferences: [ContractRef, SoapTaskRef, XmlImplRef, ResolveFacade("System.Xml.ReaderWriter")],
            usingDirectives: ["BBT.Workflow.Scripting"]);

        instance.Calc().ShouldBe(4);
    }

    [Fact]
    public void HelperRegistry_Compiles_Set_And_Exposes_Namespaces()
    {
        var sandbox = EnabledSandbox();
        var registry = new ScriptHelperRegistry(new CSharpEvaluator(sandbox), sandbox);

        var helper = new HelperSource(
            "tax-calculator", "1.0.0",
            "namespace MyHelpers { public static class TaxCalc { public static int Tax(int x) => x / 10; } }",
            "tax-calculator.csx");

        var set = registry.GetOrBuildHelpers([helper], null, [], ["System"]);

        set.Namespaces.ShouldContain("MyHelpers");
        set.FromCache.ShouldBeFalse();
    }

    [Fact]
    public void HelperRegistry_Caches_By_Content_Hash()
    {
        var sandbox = EnabledSandbox();
        var registry = new ScriptHelperRegistry(new CSharpEvaluator(sandbox), sandbox);

        var helper = new HelperSource(
            "tax-calculator", "1.0.0",
            "namespace MyHelpers { public static class TaxCalc { public static int Tax(int x) => x / 10; } }",
            "tax-calculator.csx");

        var first = registry.GetOrBuildHelpers([helper], null, [], ["System"]);
        var second = registry.GetOrBuildHelpers([helper], null, [], ["System"]);

        first.FromCache.ShouldBeFalse();
        second.FromCache.ShouldBeTrue();
        second.LoadContext.ShouldBeSameAs(first.LoadContext);
    }

    [Fact]
    public async Task Mapping_Can_Call_Referenced_Helper()
    {
        var sandbox = EnabledSandbox();
        var evaluator = new CSharpEvaluator(sandbox);
        var registry = new ScriptHelperRegistry(evaluator, sandbox);

        var helper = new HelperSource(
            "tax-calculator", "1.0.0",
            "namespace MyHelpers { public static class TaxCalc { public static int Tax(int x) => x / 10; } }",
            "tax-calculator.csx");

        var set = registry.GetOrBuildHelpers([helper], null, [], ["System"]);

        // Mapping references the helper assembly + auto-imports its namespace, compiled into the
        // helper set's load context so the call resolves at runtime.
        const string mapping = "public class C : ISandboxTestCalc { public int Calc() => MyHelpers.TaxCalc.Tax(100); }";

        var instance = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            mapping,
            extraReferences: new[] { ContractRef, set.Reference },
            usingDirectives: set.Namespaces.Append("BBT.Workflow.Scripting"),
            loadContext: set.LoadContext);

        instance.Calc().ShouldBe(10);
    }
}
