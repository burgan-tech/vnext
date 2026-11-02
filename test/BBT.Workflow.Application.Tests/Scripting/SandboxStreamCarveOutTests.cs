using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// vnext-client-sdk-core#142-A: scripts process files with System.IO streams (MemoryStream, BinaryReader,
/// StreamReader over a stream, GZip) while everything that reaches the file system, the process or a type by name
/// stays banned — including the routes the stream carve-out would otherwise open (Activator over an allowed type,
/// reflection on typeof(StreamReader) followed by a dynamic call).
/// </summary>
public class SandboxStreamCarveOutTests
{
    /// <summary>The orchestration host's shipped baseline (appsettings) plus Compression for the zip/gzip cases.</summary>
    private static ScriptSandboxOptions Sandbox() => new()
    {
        Enabled = true,
        AllowedAssemblies =
        {
            "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Linq", "System.Linq.Expressions",
            "System.Text.RegularExpressions", "Microsoft.CSharp", "System.ObjectModel", "netstandard",
        },
    };

    private static MetadataReference ContractRef => MetadataReference.CreateFromFile(typeof(ISandboxTestCalc).Assembly.Location);

    private static async Task<ISandboxTestCalc> Compile(string body, ScriptSandboxOptions? options = null)
        => (await new CSharpEvaluator(options ?? Sandbox()).CompileToInstanceAsync<ISandboxTestCalc>(
            "using System; using System.IO; using BBT.Workflow.Scripting; public class C : ISandboxTestCalc { public int Calc() { " + body + " } }",
            extraReferences: [ContractRef])).Instance;

    private static async Task Banned(string body, string expected)
    {
        var ex = await Should.ThrowAsync<ScriptCompilationException>(async () => await Compile(body));
        ex.Message.ShouldContain(expected);
    }

    [Fact]
    public async Task MemoryStream_BinaryReader_BinaryWriter_Compile_AndRun()
    {
        var calc = await Compile("""
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) { w.Write(7); w.Write(35); }
            ms.Position = 0;
            using var r = new BinaryReader(ms);
            return r.ReadInt32() + r.ReadInt32();
            """);
        calc.Calc().ShouldBe(42);
    }

    [Fact]
    public async Task StreamReaderAndWriter_OverAStream_Compile_AndRun()
    {
        var calc = await Compile("""
            var ms = new MemoryStream();
            using (var w = new StreamWriter(ms, leaveOpen: true)) { w.Write("line-1\nline-2\nline-3"); }
            ms.Seek(0, SeekOrigin.Begin);
            using var r = new StreamReader(ms);
            var n = 0; while (r.ReadLine() is not null) n++;
            return n;
            """);
        calc.Calc().ShouldBe(3);
    }

    [Fact]
    public async Task GZipStream_Compiles_WhenTheCompressionAssemblyIsGranted()
    {
        var options = Sandbox();
        options.AllowedAssemblies.Add("System.IO.Compression");
        var calc = await Compile("""
            var packed = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(new byte[1000]);
            packed.Position = 0;
            using var unzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionMode.Decompress);
            var output = new MemoryStream(); unzip.CopyTo(output);
            return (int)output.Length;
            """, options);
        calc.Calc().ShouldBe(1000);
    }

    [Theory]
    [InlineData("return File.ReadAllText(\"/etc/hosts\").Length;", "System.IO")]
    [InlineData("using var f = new FileStream(\"/etc/hosts\", FileMode.Open); return 0;", "System.IO")]
    [InlineData("return Directory.GetFiles(\"/\").Length;", "System.IO")]
    [InlineData("return Path.GetTempFileName().Length;", "System.IO")]
    [InlineData("using var r = new StreamReader(\"/etc/hosts\"); return 0;", "StreamReader")]
    [InlineData("using var w = new StreamWriter(\"/tmp/x\"); return 0;", "StreamWriter")]
    [InlineData("using var w = new StreamWriter(path: \"/tmp/x\", append: true); return 0;", "StreamWriter")]
    public async Task FileSystemAccess_StaysBanned(string body, string expected) => await Banned(body, expected);

    // Routes the carve-out would open: an allowed type handed to Activator, or its constructor reached by reflection
    // and invoked through dynamic (no symbol for the analyzer to see).
    [Theory]
    [InlineData("var r = Activator.CreateInstance(typeof(StreamReader), \"/etc/hosts\"); return 0;", "Activator")]
    [InlineData("dynamic r = Activator.CreateInstance(Type.GetType(\"System.IO.StreamReader\"), \"/etc/hosts\"); return r.ReadToEnd().Length;", "Activator")]
    [InlineData("dynamic c = typeof(StreamReader).GetConstructor(new[] { typeof(string) }); return 0;", "GetConstructor")]
    [InlineData("dynamic m = typeof(string).GetMethod(\"Trim\", Type.EmptyTypes); return 0;", "GetMethod")]
    [InlineData("var t = Type.GetType(\"System.IO.File\"); return 0;", "GetType")]
    [InlineData("var m = typeof(string).GetMembers(); return m.Length;", "GetMembers")]
    [InlineData("var f = typeof(string).GetFields(); return f.Length;", "GetFields")]
    [InlineData("var x = typeof(string).InvokeMember(\"Empty\", default, null, null, null); return 0;", "InvokeMember")]
    public async Task ReflectionRoutesToBannedTypes_AreBanned(string body, string expected) => await Banned(body, expected);

    [Theory]
    [InlineData("Environment.Exit(1); return 0;", "Exit")]
    [InlineData("Environment.FailFast(\"x\"); return 0;", "FailFast")]
    [InlineData("Environment.SetEnvironmentVariable(\"A\", \"B\"); return 0;", "SetEnvironmentVariable")]
    public async Task ProcessTermination_AndEnvironmentWrites_AreBanned(string body, string expected) => await Banned(body, expected);

    // Kept on purpose (used by shipped domain packages): environment reads (vnext-sys-flow), GetProperty, generic Activator.
    [Theory]
    [InlineData("return (Environment.GetEnvironmentVariable(\"PATH\") ?? \"\").Length > 0 ? 1 : 1;")]
    [InlineData("return typeof(string).GetProperty(\"Length\") is null ? 0 : 1;")]
    [InlineData("return Activator.CreateInstance<System.Text.StringBuilder>().Length + 1;")]
    [InlineData("return Environment.NewLine.Length > 0 ? 1 : 1;")]
    public async Task UsagesShippedPackagesRelyOn_StillCompile(string body) => (await Compile(body)).Calc().ShouldBe(1);
}

/// <summary>A mapping that reads a file, processes it through streams and writes it back compiles under the sandbox.</summary>
public class SandboxFileRoundTripCompileTests
{
    [Fact]
    public async Task ScriptBaseFileApis_WithStreams_Compile()
    {
        var options = new ScriptSandboxOptions
        {
            Enabled = true,
            AllowedAssemblies = { "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Linq", "netstandard" },
        };
        const string code = """
            using System.IO;
            using System.Threading.Tasks;
            using BBT.Workflow.Scripting;
            using BBT.Workflow.Scripting.Functions;
            public class C : ScriptBase, ISandboxTestCalc
            {
                public int Calc() => 0;
                public async Task<ScriptStoredFile> Convert(string d, string f, string i, string file)
                {
                    var source = await GetFileAsync(d, f, i, file);
                    using var reader = new BinaryReader(source.OpenRead());
                    var output = new MemoryStream();
                    using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
                        writer.Write(reader.ReadBytes((int)source.Size));
                    output.Position = 0;
                    var key = FileObjectKey(file);
                    return await PutFileAsync(d, f, i, "store", output, "out.bin", "application/octet-stream");
                }
            }
            """;
        var compiled = await new BBT.Workflow.Scripting.Evaluators.CSharpEvaluator(options).CompileToInstanceAsync<ISandboxTestCalc>(
            code, extraReferences: [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(ISandboxTestCalc).Assembly.Location),
                                    Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(BBT.Workflow.Scripting.Functions.ScriptBase).Assembly.Location)]);
        compiled.Instance.ShouldNotBeNull();
    }
}
