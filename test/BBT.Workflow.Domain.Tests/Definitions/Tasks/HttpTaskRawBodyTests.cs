using Xunit;

namespace BBT.Workflow.Definitions.Tasks;

/// <summary>
/// Unit tests for the verbatim <see cref="HttpTask.RawBody"/> escape hatch used in signing scenarios.
/// </summary>
public class HttpTaskRawBodyTests
{
    [Fact]
    public void SetRawBody_StoresVerbatimString()
    {
        var task = HttpTask.CreateEmpty();
        task.SetRawBody("a=1&b=2");
        Assert.Equal("a=1&b=2", task.RawBody);
    }

    [Fact]
    public void CloneTyped_CopiesRawBody()
    {
        var task = HttpTask.CreateEmpty();
        task.SetReference(new Reference("http-task", "test-domain", "sys-tasks", "1.0.0"));
        task.SetRawBody("SIGNED");

        var clone = task.CloneTyped();

        Assert.Equal("SIGNED", clone.RawBody);
    }

    [Fact]
    public void CopyFromInternal_CopiesRawBody()
    {
        var source = HttpTask.CreateEmpty();
        source.SetRawBody("SIGNED");
        var target = HttpTask.CreateEmpty();

        target.CopyFromInternal(source);

        Assert.Equal("SIGNED", target.RawBody);
    }

    [Fact]
    public void Reset_ClearsRawBody()
    {
        var task = HttpTask.CreateEmpty();
        task.SetRawBody("SIGNED");

        task.Reset();

        Assert.Null(task.RawBody);
    }
}
