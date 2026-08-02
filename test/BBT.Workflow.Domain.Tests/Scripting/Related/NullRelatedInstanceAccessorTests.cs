using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class NullRelatedInstanceAccessorTests
{
    [Fact]
    public void HasParent_ShouldBeFalse()
    {
        NullRelatedInstanceAccessor.Instance.HasParent.ShouldBeFalse();
    }

    [Fact]
    public async Task ParentAsync_ShouldReturnNull()
    {
        var result = await NullRelatedInstanceAccessor.Instance.ParentAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SubAsync_ShouldReturnNull()
    {
        var result = await NullRelatedInstanceAccessor.Instance.SubAsync("any-flow", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SubsAsync_ShouldReturnEmptyList()
    {
        var result = await NullRelatedInstanceAccessor.Instance.SubsAsync(null, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SubKeysAsync_ShouldReturnEmptyList()
    {
        var result = await NullRelatedInstanceAccessor.Instance.SubKeysAsync(CancellationToken.None);

        result.ShouldBeEmpty();
    }
}
