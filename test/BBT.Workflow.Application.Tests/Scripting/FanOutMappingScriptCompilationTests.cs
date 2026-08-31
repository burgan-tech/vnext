using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Scripting.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Roslyn compilation coverage for <see cref="IFanOutMapping"/> as domain teams actually author it —
/// a <c>.csx</c> body compiled through <see cref="IScriptEngine"/>'s
/// <c>CompileToInstanceAsync&lt;IFanOutMapping&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exists because <c>OutputHandler</c> was made optional by giving it a DEFAULT INTERFACE
/// IMPLEMENTATION with a nullable return, and both halves of that are script-visible. Neither is
/// verifiable from a hand-written stub in a test project: the test project compiles with
/// <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> and against the same C# version as the rest of the
/// solution, while script bodies are compiled by <c>CSharpEvaluator</c> with its own
/// <c>CSharpCompilationOptions</c> (nullable annotations OFF, only <c>DiagnosticSeverity.Error</c>
/// failing the emit) and its own parse options.
/// </para>
/// <para>
/// The two risks pinned here: (1) a pre-existing mapping declaring the OLD non-nullable
/// <c>Task&lt;ScriptResponse&gt;</c> signature must still compile and still win over the default —
/// nullability mismatch is at worst a warning, never an error, and warnings do not fail the emit;
/// (2) a mapping that omits the member entirely must compile, which only holds if the runtime
/// resolves the default interface implementation for a script-loaded type.
/// </para>
/// </remarks>
[Collection("ScriptingTests")]
public class FanOutMappingScriptCompilationTests
{
    private static ScriptEngine CreateEngine()
    {
        var evaluator = new CSharpEvaluator();
        var services = new ServiceCollection();
        using var serviceProvider = services.BuildServiceProvider();

        return new ScriptEngine(
            evaluator,
            Mock.Of<IScriptServices>(),
            new ScriptHelperRegistry(evaluator),
            new ScriptHelpersOptions { Enabled = false },
            serviceProvider,
            Mock.Of<ILogger<ScriptEngine>>());
    }

    [Fact]
    public async Task ExistingStyleMapping_DeclaringTheNonNullableOutputHandler_StillCompilesAndStillOverridesTheDefault()
    {
        // Byte-for-byte the shape shipped mappings already use: OutputHandler declared as
        // Task<ScriptResponse>, not Task<ScriptResponse?>. Widening the interface's return type to
        // nullable must not turn that into a compile error — the implementation is still an exact
        // match modulo an annotation the script compilation does not even track.
        const string code = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using BBT.Workflow.Definitions;
            using BBT.Workflow.Scripting;

            public class LegacyStyleMapping : IFanOutMapping
            {
                public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
                    => Task.FromResult(new ScriptResponse { Data = item.ItemKey });

                public Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result)
                    => Task.FromResult(new ScriptResponse
                    {
                        Data = new Dictionary<string, object> { ["authored"] = result.Total }
                    });
            }
            """;

        var mapping = await CreateEngine().CompileToInstanceAsync<IFanOutMapping>(code);

        // Compiled, and the AUTHORED body runs — not the default. A default that shadowed the
        // author's member (the classic default-interface-implementation trap, where the method is
        // only reachable through the interface) would answer null here and silently swap the
        // author's output for the runtime's packaging.
        var response = await mapping.OutputHandler(null!, new FanOutResult(3, 3, 0, false, []));

        response.ShouldNotBeNull();

        // Cast off dynamic before asserting — the authored dictionary, with the authored count.
        var data = ((object?)response!.Data).ShouldBeAssignableTo<IDictionary<string, object>>();
        data!["authored"].ShouldBe(3);

        // And its unchanged sibling still binds.
        var bound = await mapping.ItemInputHandler(null!, null!, new FanOutItem(0, null, "doc-0"));
        ((object?)bound.Data).ShouldBe("doc-0");
    }

    [Fact]
    public async Task MappingOmittingOutputHandler_Compiles_AndTheDefaultAnswersNullForDefaultPackaging()
    {
        // The whole point of the change: a fan-out over an HTTP inner task needs ItemInputHandler
        // and nothing else. Before, the abstract OutputHandler made this body a compile error, so
        // authors reimplemented the runtime's default output shape in script just to keep it.
        const string code = """
            using System.Threading.Tasks;
            using BBT.Workflow.Definitions;
            using BBT.Workflow.Scripting;

            public class BindOnlyMapping : IFanOutMapping
            {
                public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
                {
                    if (task is HttpTask http)
                    {
                        http.SetUrl($"https://items.test/{item.ItemKey}");
                    }

                    return Task.FromResult(new ScriptResponse { Data = item.ItemKey });
                }
            }
            """;

        var mapping = await CreateEngine().CompileToInstanceAsync<IFanOutMapping>(code);

        // Null from both defaults is what the executor reads as "use itemsPath" and "use the
        // default packaging" respectively.
        (await mapping.OutputHandler(null!, new FanOutResult(0, 0, 0, false, []))).ShouldBeNull();
        (await mapping.ItemSelector(null!)).ShouldBeNull();

        // The one member the author did write is the one that does the work.
        var task = WorkflowTaskFactory.CreateHttpTask("process-document");
        await mapping.ItemInputHandler(task, null!, new FanOutItem(0, null, "doc-7"));
        task.Url.ShouldBe("https://items.test/doc-7");
    }
}
