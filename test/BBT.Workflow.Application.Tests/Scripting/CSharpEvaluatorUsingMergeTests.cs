using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Pins the compile-path contracts this change established:
/// author-written using directives are MERGED with the engine-supplied ones (WithUsings used to
/// replace the list, silently dropping every author import outside the defaults), and the failure
/// taxonomy is typed — Roslyn errors throw <see cref="ScriptCompilationException"/>, sandbox
/// violations its derived <see cref="ScriptSandboxViolationException"/>.
/// </summary>
public class CSharpEvaluatorUsingMergeTests
{
    private static MetadataReference ContractRef =>
        MetadataReference.CreateFromFile(typeof(ISandboxTestCalc).Assembly.Location);

    [Fact]
    public async Task AuthorUsings_SurviveTheEngineSuppliedOnes()
    {
        // System.Globalization is NOT in the engine's default usings — before the merge fix the
        // author's `using System.Globalization;` was silently replaced and this failed with CS0246.
        var evaluator = new CSharpEvaluator();
        const string code =
            """
            using System.Globalization;

            public class UsesAuthorUsing : ISandboxTestCalc
            {
                public int Calc() => CultureInfo.InvariantCulture.NumberFormat.NumberDecimalDigits;
            }
            """;

        var result = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting", "System"]);

        result.Instance.Calc().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ADuplicateEngineUsing_DoesNotBreakTheCompile()
    {
        // The author already imports a namespace the engine also supplies — the merge must dedupe.
        var evaluator = new CSharpEvaluator();
        const string code =
            """
            using BBT.Workflow.Scripting;

            public class DuplicateUsing : ISandboxTestCalc
            {
                public int Calc() => 7;
            }
            """;

        var result = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]);

        result.Instance.Calc().ShouldBe(7);
    }

    [Fact]
    public async Task ARoslynError_ThrowsTheBaseCompilationException()
    {
        var evaluator = new CSharpEvaluator();

        var ex = await Should.ThrowAsync<ScriptCompilationException>(
            () => evaluator.CompileToInstanceAsync<object>("public class Broken { not c# }"));

        // NOT the sandbox subtype: the code is broken, not forbidden.
        ex.ShouldNotBeOfType<ScriptSandboxViolationException>();
    }

    [Fact]
    public async Task ASandboxViolation_ThrowsTheDerivedViolationException()
    {
        var evaluator = new CSharpEvaluator();
        const string code =
            "public class C { public int Calc() { var s = new System.IO.MemoryStream(); return (int)s.Length; } }";

        await Should.ThrowAsync<ScriptSandboxViolationException>(
            () => evaluator.CompileToInstanceAsync<object>(code));
    }

    [Fact]
    public async Task ASecondCallerOnTheSameKey_ReportsWaitedOrHit_NeverCompiled()
    {
        var evaluator = new CSharpEvaluator();
        const string code =
            """
            public class WaitedProbe : ISandboxTestCalc
            {
                public int Calc() => 1;
            }
            """;

        var first = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]);
        var second = await evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]);

        first.Compiled.ShouldBeTrue();
        first.Waited.ShouldBeFalse();
        second.Compiled.ShouldBeFalse();
        // Completed-entry fast path: a plain hit, not a wait.
        second.Waited.ShouldBeFalse();
    }
}
