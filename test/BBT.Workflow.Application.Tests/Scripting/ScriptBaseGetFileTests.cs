using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Functions;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// <c>ScriptBase.GetFileAsync</c> end to end through the script engine: a mapping that calls it and reads
/// <see cref="ScriptFile"/> compiles under the sandbox (production <see cref="ScriptCode"/> path) and receives the
/// DI-registered <see cref="IScriptFileReader"/> through <see cref="ScriptServices"/>.
/// </summary>
[Collection("ScriptingTests")]
public sealed class ScriptBaseGetFileTests : ApplicationTestBase<ApplicationEntryPoint>
{
    private readonly RecordingFileReader _reader = new();

    private sealed class RecordingFileReader : IScriptFileReader
    {
        public List<(string Domain, string Flow, string Instance, string File)> Calls { get; } = [];

        public Task<ScriptFile> ReadAsync(string domain, string flow, string instance, string file, CancellationToken cancellationToken)
        {
            Calls.Add((domain, flow, instance, file));
            return Task.FromResult(new ScriptFile([1, 2, 3], "p.pdf", "application/pdf", 3, "abc"));
        }
    }

    private sealed class ProbeScript : ScriptBase
    {
        public Task<ScriptFile> Read() => GetFileAsync("core", "kyc", "i-1", "f-1");
    }

    protected override void AddApplication(IServiceCollection services)
    {
        services.AddSingleton(new Mock<DaprClient>().Object);
        services.AddSingleton(Mock.Of<ILogger<ScriptServices>>());
        services.AddSingleton(Mock.Of<IConfiguration>());
        // Registered before the module: its TryAdd must not replace it.
        services.AddScoped<IScriptFileReader>(_ => _reader);
        base.AddApplication(services);
    }

    [Fact]
    public async Task MappingCallingGetFileAsync_CompilesUnderTheSandboxAndReadsThroughTheReader()
    {
        const string code = """
            using System.Threading.Tasks;
            using BBT.Workflow.Scripting;
            using BBT.Workflow.Definitions;
            using BBT.Workflow.Scripting.Functions;

            public class FileMapping : ScriptBase, IMapping
            {
                public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                {
                    ScriptFile file = await GetFileAsync("core", "kyc", "i-1", "f-1");
                    byte[] bytes = file.Content;
                    return new ScriptResponse { Data = file.Name + "|" + file.MimeType + "|" + bytes.Length + "|" + file.Size + "|" + file.ETag };
                }

                public Task<ScriptResponse> OutputHandler(ScriptContext context)
                    => Task.FromResult(new ScriptResponse());
            }
            """;

        var engine = GetRequiredService<IScriptEngine>();
        var mapping = await engine.CompileToInstanceAsync<IMapping>(ScriptCode.FromNative(code));

        var response = await mapping.InputHandler(
            WorkflowTaskFactory.CreateHttpTask(),
            new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        ((string)response.Data!).ShouldBe("p.pdf|application/pdf|3|3|abc");
        _reader.Calls.ShouldBe([("core", "kyc", "i-1", "f-1")]);
    }

    [Fact]
    public async Task WithoutAReader_GetFileAsyncThrowsInvalidOperation()
    {
        var services = new Mock<IScriptServices>();
        services.SetupGet(s => s.FileReader).Returns((IScriptFileReader?)null);
        var script = new ProbeScript();
        script.SetServices(services.Object);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => script.Read());

        ex.Message.ShouldContain("File reader is not available");
    }

    [Fact]
    public async Task BlankCoordinates_AreRejected()
    {
        var script = new BlankProbe();
        script.SetServices(new ScriptServices(
            new Mock<DaprClient>().Object, Mock.Of<ILogger<ScriptServices>>(), Mock.Of<IConfiguration>(),
            Mock.Of<IScriptSecretCache>(), _reader));

        await Should.ThrowAsync<ArgumentException>(() => script.Read());
        _reader.Calls.ShouldBeEmpty();
    }

    private sealed class BlankProbe : ScriptBase
    {
        public Task<ScriptFile> Read() => GetFileAsync("core", "kyc", " ", "f-1");
    }
}
