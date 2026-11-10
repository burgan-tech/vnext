using System;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using Xunit;

namespace BBT.Workflow;

/// <summary>
/// Runs the <see cref="InfrastructureEntryPoint"/> test classes one at a time. Each test builds an Aether
/// <c>TestBase</c>, whose configuration watches its JSON files (<c>reloadOnChange</c>); on macOS, several of
/// those FSEvents watchers starting at once in parallel test classes can deadlock in
/// <c>FileSystemWatcher.StartRaisingEvents</c> and hang the run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class InfrastructureEntryPointCollection
{
    public const string Name = "InfrastructureEntryPoint";
}

/// <summary>
/// The schema every <see cref="InfrastructureEntryPoint"/> test runs against when nothing pushed one.
/// SQLite has no schemas; <c>main</c> is the name of its primary database, so a command qualified with
/// it (as Aether's interceptor qualifies every runtime command) still resolves.
/// </summary>
internal sealed class SqliteFallbackCurrentSchema(ISchemaNameFormatter formatter) : ICurrentSchema
{
    public const string Fallback = "main";

    private readonly CurrentSchema _inner = new(formatter);

    public string? Name => _inner.Name ?? Fallback;

    public IDisposable Change(string schema) => _inner.Change(schema);
}

/// <summary>
/// Ambient unit-of-work accessor for the repository contract tests that run on
/// <see cref="InfrastructureEntryPoint"/>. Aether resolves a repository's DbContext only inside an active
/// unit of work, and those shared contract tests (<c>BBT.Workflow.Domain.Tests/Instances</c>) call the
/// repositories directly. xUnit v2 does not flow an <c>AsyncLocal</c> set during class construction or
/// fixture setup into the test method, so the fixture's unit of work is held in a plain field and served
/// whenever no unit of work was begun in the current async flow. A unit of work begun by the code under
/// test still takes precedence and restores the fallback when it ends.
/// </summary>
internal sealed class FixtureFallbackAmbientUnitOfWorkAccessor : IAmbientUnitOfWorkAccessor
{
    private readonly AsyncLocalAmbientUowAccessor _inner = new();

    public IUnitOfWork? Fallback { get; set; }

    public IUnitOfWork? Current
    {
        get => _inner.Current ?? Fallback;
        set => _inner.Current = value;
    }

    public IUnitOfWork? GetActiveUnitOfWork()
        => _inner.GetActiveUnitOfWork()
           ?? (Fallback is { IsCompleted: false, IsDisposed: false } fallback ? fallback : null);
}
