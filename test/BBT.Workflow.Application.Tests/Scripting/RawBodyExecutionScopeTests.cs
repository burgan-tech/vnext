using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// Unit tests for <see cref="RawBodyExecutionScope"/> ambient raw-body holder.
/// </summary>
public class RawBodyExecutionScopeTests
{
    [Fact]
    public void Current_IsNull_WhenNotSet()
    {
        RawBodyExecutionScope.Current.ShouldBeNull();
    }

    [Fact]
    public void Set_ExposesValue_AndRestoresOnDispose()
    {
        RawBodyExecutionScope.Current.ShouldBeNull();

        using (RawBodyExecutionScope.Set("RAW"))
        {
            RawBodyExecutionScope.Current.ShouldBe("RAW");
        }

        RawBodyExecutionScope.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Set_FlowsToNestedAsync()
    {
        using (RawBodyExecutionScope.Set("RAW"))
        {
            await Task.Yield();
            await NestedAsync();
        }

        static Task NestedAsync()
        {
            RawBodyExecutionScope.Current.ShouldBe("RAW");
            return Task.CompletedTask;
        }
    }
}
