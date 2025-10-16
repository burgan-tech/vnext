using System.Net.Http;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.States;
using BBT.Workflow.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks;

/// <summary>
/// Unit tests for SignalRTaskExecutor
/// </summary>
public class SignalRTaskExecutorTests : ApplicationTestBase<ApplicationEntryPoint>
{
    private readonly Mock<ILogger<SignalRTaskExecutor>> _loggerMock;
    private readonly Mock<IScriptEngine> _scriptEngineMock;
    private readonly Mock<IRuntimeInfoProvider> _runtimeInfoProviderMock;
    private readonly Mock<IInstanceCorrelationRepository> _instanceCorrelationRepositoryMock;
    private readonly Mock<IStateMachineService> _stateMachineServiceMock;
    private readonly Mock<IConfiguration> _configurationMock;
    private readonly IHttpClientFactory _httpClientFactory;

    public SignalRTaskExecutorTests()
    {
        _loggerMock = new Mock<ILogger<SignalRTaskExecutor>>();
        _scriptEngineMock = new Mock<IScriptEngine>();
        _runtimeInfoProviderMock = new Mock<IRuntimeInfoProvider>();
        _instanceCorrelationRepositoryMock = new Mock<IInstanceCorrelationRepository>();
        _stateMachineServiceMock = new Mock<IStateMachineService>();
        _configurationMock = new Mock<IConfiguration>();
        
        // Get HttpClientFactory from the test service provider
        _httpClientFactory = GetRequiredService<IHttpClientFactory>();
        
        // Setup default configuration
        _configurationMock.Setup(c => c["SignalR:Url"])
            .Returns("https://signalr.example.com/api/notify");
    }

    [Fact]
    public void SignalRTask_Should_Be_Created_From_Config()
    {
        // Arrange
        var config = """
                     {
                       "notificationType": 1,
                       "additionalData": {
                         "customField": "customValue"
                       },
                       "allowedHeaders": ["Authorization", "X-Correlation-Id"]
                     }
                     """;

        // Act
        var signalRTask = SignalRTask.Create(config.ToJsonElement());

        // Assert
        Assert.NotNull(signalRTask);
        Assert.Equal(SignalRTaskType.WorkflowSignalR, signalRTask.NotificationType);
        Assert.True(signalRTask.AdditionalData.HasValue);
        Assert.NotNull(signalRTask.AllowedHeaders);
        Assert.Equal(2, signalRTask.AllowedHeaders.Count);
        Assert.Contains("Authorization", signalRTask.AllowedHeaders);
        Assert.Contains("X-Correlation-Id", signalRTask.AllowedHeaders);
    }

    [Fact]
    public void SignalRTask_SetAdditionalData_Should_Work()
    {
        // Arrange
        var config = """
                     {
                       "notificationType": 1
                     }
                     """;
        var signalRTask = SignalRTask.Create(config.ToJsonElement());
        var additionalData = new { Message = "Test Message", UserId = 123 };

        // Act
        signalRTask.SetAdditionalData(additionalData);

        // Assert
        Assert.True(signalRTask.AdditionalData.HasValue);
    }

    [Fact]
    public void SignalRTask_Clone_Should_Create_Deep_Copy()
    {
        // Arrange
        var config = """
                     {
                       "notificationType": 1,
                       "additionalData": {
                         "test": "value"
                       },
                       "allowedHeaders": ["Authorization", "X-Request-Id"]
                     }
                     """;
        var original = SignalRTask.Create(config.ToJsonElement());

        // Act
        var cloned = original.CloneTyped();

        // Assert
        Assert.NotSame(original, cloned);
        Assert.Equal(original.NotificationType, cloned.NotificationType);
        Assert.Equal(original.AdditionalData, cloned.AdditionalData);
        Assert.NotSame(original.AllowedHeaders, cloned.AllowedHeaders);
        Assert.Equal(original.AllowedHeaders?.Count, cloned.AllowedHeaders?.Count);
    }

    [Fact]
    public void SignalRTask_Reset_Should_Clear_Properties()
    {
        // Arrange
        var config = """
                     {
                       "notificationType": 1,
                       "additionalData": {
                         "test": "value"
                       },
                       "allowedHeaders": ["Authorization"]
                     }
                     """;
        var task = SignalRTask.Create(config.ToJsonElement());

        // Act
        task.Reset();

        // Assert
        Assert.Equal(SignalRTaskType.WorkflowSignalR, task.NotificationType);
        Assert.False(task.AdditionalData.HasValue);
        Assert.Null(task.AllowedHeaders);
    }

    [Fact]
    public void SignalRTask_CreateEmpty_Should_Create_Empty_Instance()
    {
        // Act
        var task = SignalRTask.CreateEmpty();

        // Assert
        Assert.NotNull(task);
        Assert.Equal(SignalRTaskType.WorkflowSignalR, task.NotificationType);
        Assert.False(task.AdditionalData.HasValue);
        Assert.Null(task.AllowedHeaders);
    }

    [Fact]
    public void SignalRTask_AllowedHeaders_Should_Be_Case_Insensitive()
    {
        // Arrange
        var config = """
                     {
                       "notificationType": 1,
                       "allowedHeaders": ["authorization", "X-Correlation-ID", "x-request-id"]
                     }
                     """;

        // Act
        var task = SignalRTask.Create(config.ToJsonElement());

        // Assert
        Assert.NotNull(task.AllowedHeaders);
        Assert.Equal(3, task.AllowedHeaders.Count);
        Assert.Contains("authorization", task.AllowedHeaders);
        Assert.Contains("X-Correlation-ID", task.AllowedHeaders);
        Assert.Contains("x-request-id", task.AllowedHeaders);
    }

    [Fact]
    public void SignalRTaskExecutor_Should_Be_Creatable_With_Dependencies()
    {
        // Arrange & Act
        var executor = new SignalRTaskExecutor(
            _scriptEngineMock.Object,
            _httpClientFactory,
            _runtimeInfoProviderMock.Object,
            _instanceCorrelationRepositoryMock.Object,
            _stateMachineServiceMock.Object,
            _configurationMock.Object,
            _loggerMock.Object);

        // Assert
        Assert.NotNull(executor);
    }

    [Fact]
    public void SignalRTaskType_Enum_Should_Have_WorkflowSignalR_Value()
    {
        // Arrange & Act
        var taskType = SignalRTaskType.WorkflowSignalR;

        // Assert
        Assert.Equal(1, (int)taskType);
    }

    [Fact]
    public void TaskType_Enum_Should_Include_SignalR()
    {
        // Arrange & Act
        var taskType = TaskType.SignalR;

        // Assert
        Assert.Equal(10, (int)taskType);
        Assert.Equal("SignalR", taskType.ToString());
    }

    [Fact]
    public void GetInstanceStateOutputWithAdditionalData_Should_Inherit_From_GetInstanceStateOutput()
    {
        // Arrange
        var output = new GetInstanceStateOutputWithAdditionalData
        {
            State = "test-state",
            Status = InstanceStatus.Active,
            ETag = "test-etag",
            AdditionalData = new { TestField = "TestValue" }
        };

        // Assert
        Assert.NotNull(output);
        Assert.IsAssignableFrom<GetInstanceStateOutput>(output);
        Assert.Equal("test-state", output.State);
        Assert.Equal(InstanceStatus.Active, output.Status);
        Assert.Equal("test-etag", output.ETag);
        Assert.NotNull(output.AdditionalData);
    }
}

