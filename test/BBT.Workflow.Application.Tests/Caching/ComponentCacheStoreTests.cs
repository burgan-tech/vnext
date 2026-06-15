using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Unit tests for ComponentCacheStore
/// Tests component cache store operations for workflow entities
/// </summary>
public class ComponentCacheStoreTests
{
    private readonly Mock<ICacheSet<Definitions.Workflow>> _mockWorkflowsCacheSet;
    private readonly Mock<ICacheSet<WorkflowTask>> _mockTasksCacheSet;
    private readonly Mock<ICacheSet<SchemaDefinition>> _mockSchemasCacheSet;
    private readonly Mock<ICacheSet<Function>> _mockFunctionsCacheSet;
    private readonly Mock<ICacheSet<View>> _mockViewsCacheSet;
    private readonly Mock<ICacheSet<Extension>> _mockExtensionsCacheSet;
    private readonly ComponentCacheStore _store;

    public ComponentCacheStoreTests()
    {
        var mockCacheContext = new Mock<IDomainCacheContext>();
        _mockWorkflowsCacheSet = new Mock<ICacheSet<Definitions.Workflow>>();
        _mockTasksCacheSet = new Mock<ICacheSet<WorkflowTask>>();
        _mockSchemasCacheSet = new Mock<ICacheSet<SchemaDefinition>>();
        _mockFunctionsCacheSet = new Mock<ICacheSet<Function>>();
        _mockViewsCacheSet = new Mock<ICacheSet<View>>();
        _mockExtensionsCacheSet = new Mock<ICacheSet<Extension>>();

        mockCacheContext.Setup(x => x.Workflows).Returns(_mockWorkflowsCacheSet.Object);
        mockCacheContext.Setup(x => x.Tasks).Returns(_mockTasksCacheSet.Object);
        mockCacheContext.Setup(x => x.Schemas).Returns(_mockSchemasCacheSet.Object);
        mockCacheContext.Setup(x => x.Functions).Returns(_mockFunctionsCacheSet.Object);
        mockCacheContext.Setup(x => x.Views).Returns(_mockViewsCacheSet.Object);
        mockCacheContext.Setup(x => x.Extensions).Returns(_mockExtensionsCacheSet.Object);

        mockCacheContext.Setup(x => x.Set<Definitions.Workflow>()).Returns(_mockWorkflowsCacheSet.Object);
        mockCacheContext.Setup(x => x.Set<WorkflowTask>()).Returns(_mockTasksCacheSet.Object);
        mockCacheContext.Setup(x => x.Set<SchemaDefinition>()).Returns(_mockSchemasCacheSet.Object);
        mockCacheContext.Setup(x => x.Set<Function>()).Returns(_mockFunctionsCacheSet.Object);
        mockCacheContext.Setup(x => x.Set<View>()).Returns(_mockViewsCacheSet.Object);
        mockCacheContext.Setup(x => x.Set<Extension>()).Returns(_mockExtensionsCacheSet.Object);

        _store = new ComponentCacheStore(mockCacheContext.Object);
    }

    #region GetFlowAsync Tests

    [Fact]
    public async Task GetFlowAsync_WithVersion_ShouldReturnFlow()
    {
        // Arrange
        var domain = "test-domain";
        var key = "test-flow";
        var version = "1.0.0";
        
        var workflow = CreateMockWorkflow(key, domain, version);
        
        _mockWorkflowsCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(workflow));

        // Act
        var result = await _store.GetFlowAsync(domain, key, version, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Key.ShouldBe(key);
        result.Value.Domain.ShouldBe(domain);
        result.Value.Version.ShouldBe(version);
    }

    [Fact]
    public async Task GetFlowAsync_WithoutVersion_ShouldGetLatestVersion()
    {
        // Arrange
        var domain = "test-domain";
        var key = "test-flow";
        var workflow = CreateMockWorkflow(key, domain, "2.0.0");
        
        _mockWorkflowsCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(workflow));

        // Act
        var result = await _store.GetFlowAsync(domain, key, null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Key.ShouldBe(key);
        result.Value.Version.ShouldBe("2.0.0");
    }

    [Fact]
    public async Task GetFlowAsync_WhenNotFound_ShouldReturnNotFoundError()
    {
        // Arrange
        var domain = "test-domain";
        var key = "nonexistent-flow";
        var version = "1.0.0";
        
        _mockWorkflowsCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Fail(
                Error.NotFound("Workflow.NotFound", "Workflow not found")));

        // Act
        var result = await _store.GetFlowAsync(domain, key, version, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.NotFound);
    }

    #endregion

    #region GetTaskAsync Tests

    [Fact]
    public async Task GetTaskAsync_WithVersion_ShouldReturnTask()
    {
        // Arrange
        var domain = "test-domain";
        var key = "test-task";
        var version = "1.0.0";
        
        var task = CreateMockTask(key, domain, version);
        
        _mockTasksCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<WorkflowTask>.Ok(task));

        // Act
        var result = await _store.GetTaskAsync(domain, key, version, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Key.ShouldBe(key);
        result.Value.Domain.ShouldBe(domain);
    }

    [Fact]
    public async Task GetTaskAsync_WhenNotFound_ShouldReturnNotFoundError()
    {
        // Arrange
        var domain = "test-domain";
        var key = "nonexistent-task";
        
        _mockTasksCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<WorkflowTask>.Fail(
                Error.NotFound("WorkflowTask.NotFound", "Task not found")));

        // Act
        var result = await _store.GetTaskAsync(domain, key, null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.NotFound);
    }

    #endregion

    #region GetSchemaAsync Tests

    [Fact]
    public async Task GetSchemaAsync_WithVersion_ShouldReturnSchema()
    {
        // Arrange
        var domain = "test-domain";
        var key = "test-schema";
        var version = "1.0.0";
        
        var schema = CreateMockSchema(key, domain, version);
        
        _mockSchemasCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SchemaDefinition>.Ok(schema));

        // Act
        var result = await _store.GetSchemaAsync(domain, key, version, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Key.ShouldBe(key);
    }

    #endregion

    #region GetFunctionAsync Tests

    [Fact]
    public async Task GetFunctionAsync_WithVersion_ShouldReturnFunction()
    {
        // Arrange
        var domain = "test-domain";
        var key = "test-function";
        var version = "1.0.0";
        
        var function = CreateMockFunction(key, domain, version);
        
        _mockFunctionsCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Function>.Ok(function));

        // Act
        var result = await _store.GetFunctionAsync(domain, key, version, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Key.ShouldBe(key);
    }

    #endregion

    #region GetViewAsync Tests

    [Fact]
    public async Task GetViewAsync_WithVersion_ShouldReturnView()
    {
        // Arrange
        var domain = "test-domain";
        var key = "test-view";
        var version = "1.0.0";
        
        var view = CreateMockView(key, domain, version);
        
        _mockViewsCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<View>.Ok(view));

        // Act
        var result = await _store.GetViewAsync(domain, key, version, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Key.ShouldBe(key);
    }

    #endregion

    #region GetExtensionAsync Tests

    [Fact]
    public async Task GetExtensionAsync_WithVersion_ShouldReturnExtension()
    {
        // Arrange
        var domain = "test-domain";
        var key = "test-extension";
        var version = "1.0.0";
        
        var extension = CreateMockExtension(key, domain, version);
        
        _mockExtensionsCacheSet
            .Setup(x => x.GetByVersionAsync(domain, key, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Extension>.Ok(extension));

        // Act
        var result = await _store.GetExtensionAsync(domain, key, version, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Key.ShouldBe(key);
    }

    #endregion

    #region SetAsync Tests

    [Fact]
    public async Task SetAsync_WithWorkflow_ShouldCallWorkflowCacheSet()
    {
        // Arrange
        var workflow = CreateMockWorkflow("test", "domain", "1.0.0");
        
        _mockWorkflowsCacheSet
            .Setup(x => x.SetAsync(workflow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        // Act
        var result = await _store.SetAsync(workflow, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _mockWorkflowsCacheSet.Verify(
            x => x.SetAsync(workflow, It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    [Fact]
    public async Task SetAsync_WithTask_ShouldCallTaskCacheSet()
    {
        // Arrange
        var task = CreateMockTask("test", "domain", "1.0.0");
        
        _mockTasksCacheSet
            .Setup(x => x.SetAsync(task, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        // Act
        var result = await _store.SetAsync(task, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _mockTasksCacheSet.Verify(
            x => x.SetAsync(task, It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    #endregion

    #region Helper Methods

    private Definitions.Workflow CreateMockWorkflow(string key, string domain, string version)
    {
        var json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "target": "init"}
        }
        """;
        var flow =  System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        flow.SetReference(new Reference(key, domain, "sys-flows", version));
        return flow;
    }

    private WorkflowTask CreateMockTask(string key, string domain, string version)
    {
        var json = """
        {
            "type": "6",
            "config": {
                "url": "https://example.com",
                "method": "GET",
                "timeoutSeconds": 30,
                "validateSSL": false
            }
        }
        """;
        var task =  System.Text.Json.JsonSerializer.Deserialize<WorkflowTask>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        task.SetReference(new Reference(key, domain, "sys-tasks", version));
        return task;
    }

    private SchemaDefinition CreateMockSchema(string key, string domain, string version)
    {
        var json = """
        {
            "type": "workflow",
            "schema": {"type": "object"}
        }
        """;
        var schema =  System.Text.Json.JsonSerializer.Deserialize<SchemaDefinition>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference(key, domain, "sys-schemas", version));
        return schema;
    }

    private Function CreateMockFunction(string key, string domain, string version)
    {
        var json = $$"""
        {
            "scope": "D",
            "task": {
                "order": 1,
                "task": {
                    "key": "{{key}}",
                    "domain": "{{domain}}",
                    "version": "{{version}}",
                    "flow": "sys-tasks"
                },
                "mapping": {
                    "location": "./src/test.csx",
                    "code": "base64"
                }
            }
        }
        """;
        var function =  System.Text.Json.JsonSerializer.Deserialize<Function>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        function.SetReference(new Reference(key, domain, "sys-functions", version));
        return function;
    }

    private View CreateMockView(string key, string domain, string version)
    {
        var json = """
        {
            "type": 1,
            "target": 1,
            "content": "{}"
        }
        """;
        var view = System.Text.Json.JsonSerializer.Deserialize<View>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        view.SetReference(new Reference(key, domain, "sys-views", version));
        return view;
    }

    private Extension CreateMockExtension(string key, string domain, string version)
    {
        var json = $$"""
        {
            "type": 1,
            "scope": 1,
            "task": {
                "order": 1,
                "task": {
                    "key": "{{key}}",
                    "domain": "{{domain}}",
                    "version": "{{version}}",
                    "flow": "sys-tasks"
                },
                "mapping": {
                    "location": "./src/test.csx",
                    "code": "base64"
                }
            }
        }
        """;
        var extension =  System.Text.Json.JsonSerializer.Deserialize<Extension>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        extension.SetReference(new  Reference(key, domain, "sys-extensions", version));
        return extension;
    }

    #endregion
}
