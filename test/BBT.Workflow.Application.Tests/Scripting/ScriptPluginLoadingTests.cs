using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Helpers;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Plugin DLLs (operator- or developer-mounted drivers such as a database client) used from scripts:
/// they must load at run time for helper-less scripts too, exist once per process however many helper
/// sets reference them, and become visible without a restart once the plugin directory is populated.
/// </summary>
public sealed class ScriptPluginLoadingTests : IDisposable
{
    private const string PluginName = "Acme.Counter";

    private readonly string _pluginDirectory =
        Path.Combine(Path.GetTempPath(), "vnext-plugins-" + Guid.NewGuid().ToString("N"));

    public ScriptPluginLoadingTests()
    {
        Directory.CreateDirectory(_pluginDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
        catch
        {
            // A loaded plugin file may stay locked on some platforms; the temp directory is unique.
        }
    }

    private ScriptSandboxOptions Sandbox(int rescanIntervalSeconds = 0) => new()
    {
        Enabled = true,
        PluginDirectory = _pluginDirectory,
        PluginRescanIntervalSeconds = rescanIntervalSeconds,
        AllowedAssemblies = { "System.Private.CoreLib", "System.Runtime", "netstandard" }
    };

    private static MetadataReference ContractRef =>
        MetadataReference.CreateFromFile(typeof(ISandboxTestCalc).Assembly.Location);

    /// <summary>Compiles a tiny plugin with a static counter into the plugin directory.</summary>
    private void WriteCounterPlugin()
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileNameWithoutExtension(p) is "System.Private.CoreLib" or "System.Runtime")
            .Select(p => MetadataReference.CreateFromFile(p));

        var compilation = CSharpCompilation.Create(
            PluginName,
            [CSharpSyntaxTree.ParseText(
                "namespace Acme { public static class Counter { private static int _n; public static int Next() => ++_n; } }")],
            tpa,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var emit = compilation.Emit(Path.Combine(_pluginDirectory, PluginName + ".dll"));
        emit.Success.ShouldBeTrue(string.Join("; ", emit.Diagnostics));
    }

    [Fact]
    public async Task HelperlessScript_LoadsPluginAtRunTime()
    {
        WriteCounterPlugin();
        var evaluator = new CSharpEvaluator(Sandbox());

        var compiled = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            "public class C : ISandboxTestCalc { public int Calc() => Acme.Counter.Next(); }",
            extraReferences: [ContractRef],
            usingDirectives: ["BBT.Workflow.Scripting"],
            sandboxGrant: [PluginName]);

        compiled.Instance.Calc().ShouldBe(1);
    }

    [Fact]
    public async Task HelperSets_ShareOnePluginCopy()
    {
        WriteCounterPlugin();
        var sandbox = Sandbox();
        var evaluator = new CSharpEvaluator(sandbox);
        using var registry = new ScriptHelperRegistry(evaluator, sandbox);

        async Task<ISandboxTestCalc> MappingOver(string helperClass)
        {
            var helper = new HelperSource(
                helperClass, "1.0.0",
                $"namespace H {{ public static class {helperClass} {{ public static int Next() => Acme.Counter.Next(); }} }}",
                helperClass + ".csx");
            var set = await registry.GetOrBuildHelpersAsync([helper], [PluginName], [], ["System"]);

            var compiled = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
                $"public class C : ISandboxTestCalc {{ public int Calc() => H.{helperClass}.Next(); }}",
                extraReferences: [ContractRef, set.Reference],
                usingDirectives: ["BBT.Workflow.Scripting"],
                loadContext: set.LoadContext,
                sandboxGrant: [PluginName]);
            return compiled.Instance;
        }

        var first = await MappingOver("HelperA");
        var second = await MappingOver("HelperB");

        // One static counter ⇒ one copy of the plugin (one connection pool for a database driver).
        first.Calc().ShouldBe(1);
        second.Calc().ShouldBe(2);
    }

    /// <summary>
    /// Compatibility pin. Helper sets used to preload every plugin DLL into their own context, and a
    /// context serves its own loaded assemblies first — so a plugin DLL named like a host/framework
    /// assembly (a driver's dependency, typically) won over the host copy inside a helper set. That must
    /// stay so. A helper-less script never saw plugins, so for it a host name keeps resolving to the host.
    /// </summary>
    [Fact]
    public void FrameworkNamedPluginDll_WinsInHelperSets_AndNotInHelperlessScripts()
    {
        const string frameworkName = "System.Xml.XPath.XDocument";
        SandboxedReferenceSet.IsFrameworkAssembly(frameworkName).ShouldBeTrue();

        var compilation = CSharpCompilation.Create(
            frameworkName,
            [CSharpSyntaxTree.ParseText("namespace Fake { public static class Marker { } }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var pluginPath = Path.Combine(_pluginDirectory, frameworkName + ".dll");
        compilation.Emit(pluginPath).Success.ShouldBeTrue();

        var plugins = ScriptPluginLoadContext.For(Sandbox())!;
        var name = new System.Reflection.AssemblyName(frameworkName);

        plugins.Resolve(name, includeFrameworkNames: true)!.Location.ShouldBe(pluginPath);
        plugins.Resolve(name, includeFrameworkNames: false).ShouldBeNull();
    }

    [Fact]
    public void PluginAddedAfterFirstLookup_IsFoundOnRescan()
    {
        var options = Sandbox(rescanIntervalSeconds: 0);
        SandboxedReferenceSet.IsResolvable(options, PluginName).ShouldBeFalse();

        WriteCounterPlugin();

        SandboxedReferenceSet.IsResolvable(options, PluginName).ShouldBeTrue();
    }

    [Fact]
    public void PluginAddedAfterFirstLookup_IsNotRescannedWithinTheInterval()
    {
        var options = Sandbox(rescanIntervalSeconds: 3600);
        SandboxedReferenceSet.IsResolvable(options, PluginName).ShouldBeFalse();

        WriteCounterPlugin();

        SandboxedReferenceSet.IsResolvable(options, PluginName).ShouldBeFalse();
    }

    [Fact]
    public void ScriptContext_CarriesCancellationToken_IntoParallelBranches()
    {
        using var cts = new CancellationTokenSource();
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetCancellationToken(cts.Token)
            .Build();

        context.CancellationToken.ShouldBe(cts.Token);
        context.CreateParallelBranch().CancellationToken.ShouldBe(cts.Token);
    }
}
