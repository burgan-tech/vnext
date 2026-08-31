using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Diagnostic (non-asserting) measurement harness for the Roslyn compile pipeline. Run before and
/// after a compile-path change and compare the printed numbers — it exists so a claim like
/// "template reuse cut per-miss time" is a measurement, not an opinion. Timing assertions are
/// deliberately absent: wall-clock numbers on shared CI hardware are not a contract.
/// </summary>
public class ScriptCompilePerfHarness(ITestOutputHelper output)
{
    private static MetadataReference ContractRef =>
        MetadataReference.CreateFromFile(typeof(ISandboxTestCalc).Assembly.Location);

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
        BannedNamespaces = []
    };

    private static string Code(string prefix, int i) =>
        $$"""
        public class {{prefix}}{{i}} : ISandboxTestCalc
        {
            public int Calc()
            {
                var list = new System.Collections.Generic.List<int>();
                for (var j = 0; j < {{i + 3}}; j++) list.Add(j * {{i + 1}});
                return System.Linq.Enumerable.Sum(list) + {{i}};
            }
        }
        """;

    [Fact]
    public async Task MeasureCompilePipeline()
    {
        // ── non-sandbox path (Execution-host shape) ────────────────────────────────
        var plain = new CSharpEvaluator();

        var cold = await TimeAsync(() => CompileAsync(plain, Code("Cold", 0)));
        output.WriteLine($"plain.cold_first_compile_ms={cold:F1}");

        var plainMisses = new List<double>();
        for (var i = 1; i <= 10; i++)
            plainMisses.Add(await TimeAsync(() => CompileAsync(plain, Code("Plain", i))));
        Report("plain.warm_miss", plainMisses);

        var hit = await TimeAsync(() => CompileAsync(plain, Code("Plain", 1)));
        output.WriteLine($"plain.hit_ms={hit:F3}");

        // ── sandbox path (Orchestration-host shape: reference-set build + analyzer) ─
        var sandboxed = new CSharpEvaluator(EnabledSandbox());

        var sandboxMisses = new List<double>();
        for (var i = 1; i <= 10; i++)
            sandboxMisses.Add(await TimeAsync(() => CompileAsync(sandboxed, Code("Sbx", i))));
        Report("sandbox.warm_miss", sandboxMisses);

        var sandboxHit = await TimeAsync(() => CompileAsync(sandboxed, Code("Sbx", 1)));
        output.WriteLine($"sandbox.hit_ms={sandboxHit:F3}");
    }

    private static Task CompileAsync(CSharpEvaluator evaluator, string code) =>
        evaluator.CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [ContractRef], usingDirectives: ["BBT.Workflow.Scripting"]);

    private static async Task<double> TimeAsync(Func<Task> action)
    {
        var sw = Stopwatch.GetTimestamp();
        await action();
        return Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
    }

    private void Report(string label, List<double> samples)
    {
        var sorted = samples.OrderBy(s => s).ToList();
        output.WriteLine(
            $"{label}: median_ms={sorted[sorted.Count / 2]:F1} " +
            $"min_ms={sorted[0]:F1} max_ms={sorted[^1]:F1} " +
            $"mean_ms={samples.Average():F1} n={samples.Count}");
    }
}
