using System;
using System.Runtime.Loader;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// vnext#1268: a script's frames in a stack trace carry the line in the author's own source — the
/// incident's trace is only useful if it points at the line that threw. A script has no file name
/// (its compile errors read as they always have); a helper's frames name the helper's file. The
/// engine's using directives must not shift the lines, neither the ones merged into a script nor the
/// prefix put in front of a helper.
/// </summary>
public class CSharpEvaluatorStackTraceTests
{
    private static MetadataReference ContractRef =>
        MetadataReference.CreateFromFile(typeof(ISandboxTestCalc).Assembly.Location);

    private static readonly string[] EngineUsings = ["BBT.Workflow.Scripting", "System", "System.Linq"];

    private static string TraceOf(ISandboxTestCalc script) =>
        Should.Throw<InvalidOperationException>(() => script.Calc()).ToString();

    [Fact]
    public async Task ScriptFrames_CarryTheLinesOfItsSource()
    {
        const string code = """
            public class LineProbe : ISandboxTestCalc
            {
                public int Calc()
                {
                    return Fail(3);
                }

                private static int Fail(int value)
                {
                    throw new InvalidOperationException("line probe " + value);
                }
            }
            """;

        var compiled = await new CSharpEvaluator().CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: EngineUsings);
        var trace = TraceOf(compiled.Instance);

        trace.ShouldContain("LineProbe.Fail(Int32 value) in :line 10");
        trace.ShouldContain("LineProbe.Calc() in :line 5");
    }

    [Fact]
    public async Task MergedUsings_DoNotShiftTheAuthorsLines()
    {
        const string code = """
            using System.Globalization;

            public class MergedUsingProbe : ISandboxTestCalc
            {
                public int Calc() => throw new InvalidOperationException(CultureInfo.InvariantCulture.Name);
            }
            """;

        var compiled = await new CSharpEvaluator().CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: EngineUsings);

        TraceOf(compiled.Instance).ShouldContain("MergedUsingProbe.Calc() in :line 5");
    }

    [Fact]
    public async Task HelperFrames_NameTheHelperFile_AndItsOwnLines_AfterTheUsingPrefix()
    {
        const string helper = """
            public static class LineProbeHelper
            {
                public static int Half(int value)
                {
                    throw new InvalidOperationException("helper " + value);
                }
            }
            """;
        const string code = """
            public class UsesLineProbeHelper : ISandboxTestCalc
            {
                public int Calc() => LineProbeHelper.Half(4);
            }
            """;
        var evaluator = new CSharpEvaluator();
        var context = new AssemblyLoadContext($"line-probe-{Guid.NewGuid():N}");
        var helpers = evaluator.CompileHelpers(
            [("LineProbeHelper.csx", helper)], context, extraReferences: [ContractRef], usingDirectives: EngineUsings);

        var compiled = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef, helpers.Reference], usingDirectives: EngineUsings, loadContext: context);
        var trace = TraceOf(compiled.Instance);

        trace.ShouldContain("LineProbeHelper.Half(Int32 value) in LineProbeHelper.csx:line 5");
        trace.ShouldContain("UsesLineProbeHelper.Calc() in :line 3");
    }

    [Fact]
    public async Task CompileErrors_ReadAsBefore_WithoutAFileName()
    {
        const string code = """
            public class Broken : ISandboxTestCalc
            {
                public int Calc() => missing + 1;
            }
            """;

        var failure = await Should.ThrowAsync<ScriptCompilationException>(() =>
            new CSharpEvaluator().CompileToInstanceAsync<ISandboxTestCalc>(
                code, extraReferences: [ContractRef], usingDirectives: EngineUsings));

        failure.Message.ShouldContain("\n(3,26): error CS0103");
    }

    [Fact]
    public void HelperCompileErrors_ReportTheHelpersOwnLines_AfterTheUsingPrefix()
    {
        const string helper = """
            public static class BrokenLineProbeHelper
            {
                public static int Half(int value) => missing / 2;
            }
            """;

        var failure = Should.Throw<ScriptCompilationException>(() => new CSharpEvaluator().CompileHelpers(
            [("BrokenLineProbeHelper.csx", helper)], new AssemblyLoadContext($"broken-{Guid.NewGuid():N}"),
            extraReferences: [ContractRef], usingDirectives: EngineUsings));

        failure.Message.ShouldContain("BrokenLineProbeHelper.csx(3,42): error CS0103");
    }
}
