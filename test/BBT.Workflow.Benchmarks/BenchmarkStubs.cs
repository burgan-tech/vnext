using BBT.Workflow.Scripting.Functions;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Hand-written no-op <see cref="IScriptServices"/> for the engine-level benchmarks in
/// <see cref="CompileHitPathIdentityBenchmarks"/>. Compile-only exercise (no compiled script's
/// <c>Handler</c> is ever invoked here), so none of these members are actually read at runtime —
/// they only need to satisfy the interface so <c>ScriptActivator.Create</c>'s
/// <c>ScriptBase.SetServices</c> call has something to store. A Moq-backed double is deliberately
/// avoided: BenchmarkDotNet runs the measured methods in a separate, isolated toolchain process
/// where pulling in Moq (an Application.Tests-only dependency) would be an unnecessary reference.
/// </summary>
public sealed class NoopScriptServices : IScriptServices
{
    public DaprClient DaprClient => null!;

    public ILogger Logger => NullLogger.Instance;

    public IConfiguration Configuration => null!;

    // IScriptSecretCache? SecretCache uses the interface's own default (=> null); no override needed.
}

