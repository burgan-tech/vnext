using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// The call the multipart docs promise a mapping script — decode base64, hand the bytes to
/// <see cref="HttpTask.AddMultipartFile"/> — compiles and runs under the orchestration host's shipped sandbox
/// baseline, while the HTTP stack a script would need to upload by itself stays banned.
/// </summary>
public class SandboxHttpMultipartTests
{
    /// <summary>The orchestration host's shipped baseline (appsettings.json Scripting:Sandbox).</summary>
    private static ScriptSandboxOptions Sandbox() => new()
    {
        Enabled = true,
        AllowedAssemblies =
        {
            "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Linq", "System.Linq.Expressions",
            "System.Text.RegularExpressions", "Microsoft.CSharp", "System.ObjectModel", "netstandard",
        },
    };

    // The engine hands scripts the platform contracts as explicit references; the test does the same.
    private static readonly MetadataReference[] Contracts =
    [
        MetadataReference.CreateFromFile(typeof(ISandboxTestCalc).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(HttpTask).Assembly.Location),
    ];

    private static async Task<ISandboxTestCalc> Compile(string body) =>
        (await new CSharpEvaluator(Sandbox()).CompileToInstanceAsync<ISandboxTestCalc>(
            "using System; using BBT.Workflow.Definitions; using BBT.Workflow.Scripting; " +
            "public class C : ISandboxTestCalc { public int Calc() { " + body + " } }",
            extraReferences: Contracts)).Instance;

    [Fact]
    public async Task Base64ToMultipartFile_CompilesAndRuns_InTheSandbox()
    {
        var calc = await Compile("""
            var task = HttpTask.CreateEmpty();
            var bytes = Convert.FromBase64String("JVBERi0xLjQK4uPP0w==");   // "%PDF-1.4\n" + 4 high bytes
            task.AddMultipartFile("file", bytes, "cek.pdf", "application/pdf");
            task.AddMultipartField("source", "factoring");
            return task.Multipart!.Count * 100 + task.Multipart[0].Content!.Length;
            """);

        calc.Calc().ShouldBe(213);
    }

    [Fact]
    public async Task UploadingWithoutTheTask_StaysBanned()
    {
        var ex = await Should.ThrowAsync<ScriptCompilationException>(async () => await Compile(
            "using var c = new System.Net.Http.HttpClient(); return 0;"));

        ex.Message.ShouldContain("System.Net");
    }
}
