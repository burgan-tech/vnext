using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Data;

/// <summary>
/// The command span names the schema the command ran against, not Aether's placeholder, and keeps the
/// instrumentation's sanitised text otherwise unchanged.
/// </summary>
public sealed class SchemaPlaceholderStatementTagInterceptorTests
{
    private static readonly ActivitySource Source = new(nameof(SchemaPlaceholderStatementTagInterceptorTests));

    static SchemaPlaceholderStatementTagInterceptorTests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = s => s.Name == nameof(SchemaPlaceholderStatementTagInterceptorTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    [Theory]
    // Npgsql leaves the all-lowercase placeholder unquoted; raw SQL may quote it.
    [InlineData("db.statement", "__aether_schema__")]
    [InlineData("db.query.text", "__aether_schema__")]
    [InlineData("db.statement", "\"__aether_schema__\"")]
    public async Task ReplacesThePlaceholderInTheQueryTextTag(string tag, string placeholder)
    {
        using var activity = Source.StartActivity("Db.SELECT")!;
        activity.SetTag(tag,
            $"SELECT i.\"Id\" FROM {placeholder}.\"Instances\" AS i JOIN {placeholder}.\"InstancesData\" AS d ON i.\"Id\" = d.\"InstanceId\" WHERE i.\"Status\" = ?");

        await new SchemaPlaceholderStatementTagInterceptor("sys_version_registry")
            .ReaderExecutingAsync(null!, null!, default);

        activity.GetTagItem(tag).ShouldBe(
            "SELECT i.\"Id\" FROM \"sys_version_registry\".\"Instances\" AS i JOIN \"sys_version_registry\".\"InstancesData\" AS d ON i.\"Id\" = d.\"InstanceId\" WHERE i.\"Status\" = ?");
    }

    [Fact]
    public void LeavesThePlaceholderOutsideSchemaPositionAlone()
    {
        using var activity = Source.StartActivity("Db.SELECT")!;
        const string Text = "SELECT x__aether_schema__.\"Id\", '__aether_schema__' FROM __aether_schema__.\"Instances\"";
        activity.SetTag("db.statement", Text);

        new SchemaPlaceholderStatementTagInterceptor("flow_a").ScalarExecuting(null!, null!, default);

        activity.GetTagItem("db.statement").ShouldBe(
            "SELECT x__aether_schema__.\"Id\", '__aether_schema__' FROM \"flow_a\".\"Instances\"");
    }

    [Fact]
    public void LeavesATagWithoutThePlaceholderUntouched()
    {
        using var activity = Source.StartActivity("Db.SELECT")!;
        const string Text = "SELECT 1 FROM \"sys_flows\".\"Instances\"";
        activity.SetTag("db.statement", Text);

        new SchemaPlaceholderStatementTagInterceptor("flow_a").ReaderExecuting(null!, null!, default);

        activity.GetTagItem("db.statement").ShouldBe(Text);
    }

    [Fact]
    public async Task NoActiveSpan_DoesNothing()
    {
        Activity.Current = null;

        var result = await new SchemaPlaceholderStatementTagInterceptor("flow_a")
            .NonQueryExecutingAsync(null!, null!, InterceptionResult<int>.SuppressWithResult(7));

        result.Result.ShouldBe(7);
    }
}
