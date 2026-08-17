using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Functions;
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

    [Fact]
    public async Task Sandbox_Allows_Uri_Escaping_With_Uri_Impl_Referenced()
    {
        // Regression: System.Uri lives in System.Private.Uri — the System.Runtime facade only
        // type-forwards it, so omitting the implementation reference surfaced as CS0103 on any
        // mapping calling Uri.EscapeDataString (ubiquitous in domain query-string composition).
        // Mirrors the engine's reference contract, which always includes the Uri implementation.
        var evaluator = new CSharpEvaluator(EnabledSandbox());
        const string code =
            "public class C : ISandboxTestCalc { public int Calc() => System.Uri.EscapeDataString(\"a b\").Length; }";

        var instance = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code,
            extraReferences: [ContractRef, MetadataReference.CreateFromFile(typeof(System.Uri).Assembly.Location)],
            usingDirectives: ["BBT.Workflow.Scripting"]);

        instance.Calc().ShouldBe(5); // "a%20b"
    }

    [Fact]
    public async Task Sandbox_Rejects_Uri_Without_Uri_Impl_Reference()
    {
        // Documents the failure mode this fix addresses: with only the granted facades, Uri does
        // not resolve (CS0103/CS0012) because no referenced assembly carries the implementation.
        var evaluator = new CSharpEvaluator(EnabledSandbox());
        const string code =
            "public class C : ISandboxTestCalc { public int Calc() => System.Uri.EscapeDataString(\"a b\").Length; }";

        var ex = await Should.ThrowAsync<System.InvalidOperationException>(async () =>
            await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
                code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]));

        ex.Message.ShouldContain("Uri");
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

    [Fact]
    public void HelperRegistry_Does_Not_Cache_A_Failed_Build()
    {
        // Regression: the registry stored the build in a Lazy<T>, which caches the factory's exception.
        // Because the registry is a singleton, one transient failure (e.g. an OperationCanceledException
        // from a torn-down request) was replayed for the whole process lifetime and the helper set could
        // never be compiled again. The faulted entry must be evicted instead.
        var sandbox = EnabledSandbox();
        var evaluator = new FailOnceEvaluator(new CSharpEvaluator(sandbox));
        var registry = new ScriptHelperRegistry(evaluator, sandbox);

        Should.Throw<OperationCanceledException>(() =>
        {
            registry.GetOrBuildHelpers([TaxHelper], null, [], ["System"]);
        });

        // Second attempt must reach the evaluator again rather than replaying the cached exception.
        var set = registry.GetOrBuildHelpers([TaxHelper], null, [], ["System"]);

        set.Namespaces.ShouldContain("MyHelpers");
        set.FromCache.ShouldBeFalse();
        evaluator.Attempts.ShouldBe(2);
    }

    [Fact]
    public void HelperRegistry_Does_Not_Pass_Caller_Token_To_The_Compiler()
    {
        // The helper set is a process-wide artifact, so a build in progress must not be cancellable by
        // the request that happened to trigger it: the compile runs with CancellationToken.None.
        var sandbox = EnabledSandbox();
        var evaluator = new TokenCapturingEvaluator(new CSharpEvaluator(sandbox));
        var registry = new ScriptHelperRegistry(evaluator, sandbox);

        using var cts = new CancellationTokenSource();

        var set = registry.GetOrBuildHelpers([TaxHelper], null, [], ["System"], cts.Token);

        set.Namespaces.ShouldContain("MyHelpers");
        evaluator.ObservedToken.CanBeCanceled.ShouldBeFalse();
    }

    [Fact]
    public void HelperRegistry_Rejects_An_Already_Cancelled_Caller_Before_Compiling()
    {
        // An abandoned caller should not kick off an expensive shared compile at all.
        var sandbox = EnabledSandbox();
        var evaluator = new TokenCapturingEvaluator(new CSharpEvaluator(sandbox));
        var registry = new ScriptHelperRegistry(evaluator, sandbox);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() =>
        {
            registry.GetOrBuildHelpers([TaxHelper], null, [], ["System"], cts.Token);
        });

        evaluator.Attempts.ShouldBe(0);
    }

    private static HelperSource TaxHelper => new(
        "tax-calculator", "1.0.0",
        "namespace MyHelpers { public static class TaxCalc { public static int Tax(int x) => x / 10; } }",
        "tax-calculator.csx");

    /// <summary>
    /// Delegating evaluator that simulates a cancelled first helper compilation and then succeeds,
    /// so the registry's poisoned-entry eviction can be observed.
    /// </summary>
    private sealed class FailOnceEvaluator(IEvaluator inner) : DelegatingEvaluator(inner)
    {
        public override CompiledHelpers CompileHelpers(
            IReadOnlyList<(string Path, string Code)> sources,
            AssemblyLoadContext loadContext,
            IEnumerable<MetadataReference>? extraReferences,
            IEnumerable<string>? usingDirectives,
            IReadOnlyList<string>? sandboxGrant,
            CancellationToken cancellationToken)
        {
            Attempts++;

            if (Attempts == 1)
                throw new OperationCanceledException("The operation was canceled.");

            return Inner.CompileHelpers(
                sources, loadContext, extraReferences, usingDirectives, sandboxGrant, cancellationToken);
        }
    }

    /// <summary>
    /// Delegating evaluator that records the token the registry hands to the compiler.
    /// </summary>
    private sealed class TokenCapturingEvaluator(IEvaluator inner) : DelegatingEvaluator(inner)
    {
        public CancellationToken ObservedToken { get; private set; }

        public override CompiledHelpers CompileHelpers(
            IReadOnlyList<(string Path, string Code)> sources,
            AssemblyLoadContext loadContext,
            IEnumerable<MetadataReference>? extraReferences,
            IEnumerable<string>? usingDirectives,
            IReadOnlyList<string>? sandboxGrant,
            CancellationToken cancellationToken)
        {
            Attempts++;
            ObservedToken = cancellationToken;

            return Inner.CompileHelpers(
                sources, loadContext, extraReferences, usingDirectives, sandboxGrant, cancellationToken);
        }
    }

    /// <summary>
    /// Test-only <see cref="IEvaluator"/> base that forwards to a real evaluator, letting each test
    /// override only the helper-compilation behaviour it cares about.
    /// </summary>
    private abstract class DelegatingEvaluator(IEvaluator inner) : IEvaluator
    {
        protected IEvaluator Inner { get; } = inner;

        public int Attempts { get; protected set; }

        public Task<T> CompileToInstanceAsync<T>(
            string code,
            IScriptServices? services = null,
            IEnumerable<MetadataReference>? extraReferences = null,
            IEnumerable<string>? usingDirectives = null,
            CancellationToken cancellationToken = default,
            AssemblyLoadContext? loadContext = null,
            IReadOnlyList<string>? sandboxGrant = null)
            => Inner.CompileToInstanceAsync<T>(
                code, services, extraReferences, usingDirectives, cancellationToken, loadContext, sandboxGrant);

        // Declared without default values: defaults are not part of the signature, so this still
        // implements IEvaluator.CompileHelpers, and every call site here passes all arguments.
        public abstract CompiledHelpers CompileHelpers(
            IReadOnlyList<(string Path, string Code)> sources,
            AssemblyLoadContext loadContext,
            IEnumerable<MetadataReference>? extraReferences,
            IEnumerable<string>? usingDirectives,
            IReadOnlyList<string>? sandboxGrant,
            CancellationToken cancellationToken);
    }
}
