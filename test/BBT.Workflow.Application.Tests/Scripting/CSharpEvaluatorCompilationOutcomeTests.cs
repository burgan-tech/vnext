using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

public class CSharpEvaluatorCompilationOutcomeTests
{
    public interface IOutcomeProbe
    {
        int Run();
    }

    private const string ProbeSource = """
        public class OutcomeProbe : BBT.Workflow.Scripting.CSharpEvaluatorCompilationOutcomeTests.IOutcomeProbe
        {
            public int Run() => 42;
        }
        """;

    private static Microsoft.CodeAnalysis.MetadataReference[] ProbeReferences() =>
    [
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
            System.Reflection.Assembly.Load("System.Runtime").Location),
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
            typeof(CSharpEvaluatorCompilationOutcomeTests).Assembly.Location)
    ];

    [Fact]
    public async Task FirstCompile_ReportsCompiledTrue_WithPositiveDuration()
    {
        var evaluator = new CSharpEvaluator();

        var outcome = await evaluator.CompileToInstanceAsync<IOutcomeProbe>(
            ProbeSource, extraReferences: ProbeReferences());

        outcome.Compiled.ShouldBeTrue();
        outcome.CompileDuration.ShouldBeGreaterThan(TimeSpan.Zero);
        outcome.Instance.Run().ShouldBe(42);
    }

    [Fact]
    public async Task SecondIdenticalCompile_ReportsCompiledFalse()
    {
        var evaluator = new CSharpEvaluator();
        _ = await evaluator.CompileToInstanceAsync<IOutcomeProbe>(
            ProbeSource, extraReferences: ProbeReferences());

        var second = await evaluator.CompileToInstanceAsync<IOutcomeProbe>(
            ProbeSource, extraReferences: ProbeReferences());

        second.Compiled.ShouldBeFalse();
        second.CompileDuration.ShouldBe(TimeSpan.Zero);
        second.Instance.Run().ShouldBe(42);
    }

    [Fact]
    public async Task ConcurrentIdenticalCompiles_ExactlyOneReportsCompiled()
    {
        var evaluator = new CSharpEvaluator();
        // Farklı kaynak: diğer testlerin cache'iyle çakışmasın diye nonce'lu.
        var source = ProbeSource.Replace("42", "43");

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            evaluator.CompileToInstanceAsync<IOutcomeProbe>(
                source, extraReferences: ProbeReferences()))).ToArray();
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Count(o => o.Compiled).ShouldBe(1);
        outcomes.ShouldAllBe(o => o.Instance.Run() == 43);
    }

    [Fact]
    public void CachedTypeCount_IsExposedOnInterface()
    {
        IEvaluator evaluator = new CSharpEvaluator();
        evaluator.CachedTypeCount.ShouldBe(0);
    }
}
