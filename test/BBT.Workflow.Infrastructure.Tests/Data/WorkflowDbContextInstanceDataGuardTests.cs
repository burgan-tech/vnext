using System;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Workflow.Data;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// Unit tests for the InstanceData safety guard in <see cref="WorkflowDbContext.SaveChangesAsync"/>:
/// after the centralized write funnel was replaced by the explicit
/// <see cref="IInstanceDataWriteService"/>, the context performs NO versioning work — it only
/// refuses to save a new InstanceData row whose VersionNo was never assigned, which is the
/// signature of a code path that bypassed the service.
/// <para>
/// The guard runs BEFORE the base SaveChanges touches the database, so these tests use the
/// Npgsql model (jsonb mappings) without any live connection: the throwing path never reaches
/// the database, and the passing path is asserted by the exception NOT being the guard's
/// (the save proceeds to the — deliberately unreachable — database instead).
/// </para>
/// </summary>
public sealed class WorkflowDbContextInstanceDataGuardTests : IDisposable
{
    private readonly WorkflowDbContext _context;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public WorkflowDbContextInstanceDataGuardTests()
    {
        // Domain methods carry aspects that resolve through the ambient provider; a workflow-less
        // context makes the schema-validation aspect a no-op.
        _ambient = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IWorkflowContext>(new NullWorkflowContext())
            .BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        // Unreachable on purpose: the model builds, the change tracker works, and any attempt
        // to actually reach the database fails fast with a connection error.
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1")
            .Options;

        _context = new WorkflowDbContext(options);
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        _context.Dispose();
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task SaveChangesAsync_AddedInstanceDataWithoutVersionNo_ThrowsBeforeTouchingTheDatabase()
    {
        var instance = CreateInstanceWithData();

        _context.Instances.Add(instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _context.SaveChangesAsync());
        ex.Message.ShouldContain("IInstanceDataWriteService");
    }

    [Fact]
    public async Task SaveChangesAsync_AddedInstanceDataWithAssignedVersionNo_PassesTheGuard()
    {
        var instance = CreateInstanceWithData();
        foreach (var data in instance.DataList)
        {
            data.VersionNo = 1; // what the write service assigns under the row lock
        }

        _context.Instances.Add(instance);

        // The guard lets the save through; it then fails on the unreachable database —
        // proving the failure is a connection error, NOT the guard's exception.
        var ex = await Should.ThrowAsync<Exception>(() => _context.SaveChangesAsync());
        ex.Message.ShouldNotContain("IInstanceDataWriteService");
    }

    private static Instance CreateInstanceWithData()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0");
        instance.AddData(Guid.NewGuid(), new JsonData("{\"a\":1}"), VersionStrategy.IncreasePatch);
        return instance;
    }

    private sealed class NullWorkflowContext : IWorkflowContext
    {
        public BBT.Workflow.Definitions.Workflow? Workflow => null;
        public bool HasWorkflow => false;
        public void SetWorkflow(BBT.Workflow.Definitions.Workflow workflow) { }
    }
}
