using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.CastHandlers;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Unit tests for ViewWorkflowCastHandler
/// Tests view workflow casting operations
/// </summary>
public class ViewWorkflowCastHandlerTests
{
    private readonly Mock<IDomainCacheContext> _mockCacheContext;
    private readonly Mock<ICacheSet<View>> _mockViewsCacheSet;
    private readonly ViewWorkflowCastHandler _handler;

    public ViewWorkflowCastHandlerTests()
    {
        _mockCacheContext = new Mock<IDomainCacheContext>();
        _mockViewsCacheSet = new Mock<ICacheSet<View>>();
        
        _mockCacheContext.Setup(x => x.Views).Returns(_mockViewsCacheSet.Object);
        
        _handler = new ViewWorkflowCastHandler(_mockCacheContext.Object);
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_ForSysViews()
    {
        // Act
        var result = _handler.CanHandle("sys-views");

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_ShouldReturnFalse_ForOtherWorkflowTypes()
    {
        // Arrange & Act & Assert
        _handler.CanHandle("sys-tasks").ShouldBeFalse();
        _handler.CanHandle("sys-flows").ShouldBeFalse();
        _handler.CanHandle("sys-schemas").ShouldBeFalse();
        _handler.CanHandle("sys-functions").ShouldBeFalse();
        _handler.CanHandle("sys-extensions").ShouldBeFalse();
        _handler.CanHandle("unknown").ShouldBeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldDeserializeAndCacheView()
    {
        // Arrange
        var reference = new Reference("test-view", "test-domain", "sys-views", "1.0.0");
        
        var viewJson = """
        {
            "type": 1,
            "target": 1,
            "content": "{}"
        }
        """;
        
        var attributes = JsonDocument.Parse(viewJson).RootElement;

        _mockViewsCacheSet
            .Setup(x => x.SetAsync(It.IsAny<View>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        // Act
        await _handler.HandleAsync(reference, attributes, CancellationToken.None);

        // Assert
        _mockViewsCacheSet.Verify(
            x => x.SetAsync(It.Is<View>(v => 
                v.Key == "test-view" && 
                v.Domain == "test-domain" && 
                v.Version == "1.0.0"), 
                It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ShouldDeserializeAndCacheView_WithRenderer()
    {
        // Arrange
        var reference = new Reference("renderer-view", "test-domain", "sys-views", "2.0.0");

        var viewJson = """
        {
            "type": 1,
            "content": "{}",
            "display": "test-display",
            "renderer": "flutter"
        }
        """;

        var attributes = JsonDocument.Parse(viewJson).RootElement;

        _mockViewsCacheSet
            .Setup(x => x.SetAsync(It.IsAny<View>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        // Act
        await _handler.HandleAsync(reference, attributes, CancellationToken.None);

        // Assert
        _mockViewsCacheSet.Verify(
            x => x.SetAsync(It.Is<View>(v =>
                v.Key == "renderer-view" &&
                v.Domain == "test-domain" &&
                v.Version == "2.0.0" &&
                v.Renderer == "flutter"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ShouldDeserializeAndCacheView_WithoutRenderer()
    {
        // Arrange
        var reference = new Reference("no-renderer-view", "test-domain", "sys-views", "1.0.0");

        var viewJson = """
        {
            "type": 1,
            "content": "{}",
            "display": "test-display"
        }
        """;

        var attributes = JsonDocument.Parse(viewJson).RootElement;

        _mockViewsCacheSet
            .Setup(x => x.SetAsync(It.IsAny<View>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        // Act
        await _handler.HandleAsync(reference, attributes, CancellationToken.None);

        // Assert
        _mockViewsCacheSet.Verify(
            x => x.SetAsync(It.Is<View>(v =>
                v.Key == "no-renderer-view" &&
                v.Renderer == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
