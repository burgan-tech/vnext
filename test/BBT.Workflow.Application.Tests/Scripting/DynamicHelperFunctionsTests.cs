using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Functions;
using Dapr.Client;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Tests for dynamic data helper functions available via <see cref="ScriptBase"/>.
/// Verifies collection helpers (ListFilter, ListFirst, ListLast, ListAny, ListCount,
/// ListSelect, ListAdd, ListRemove) and object helpers (CreateObject, CreateList,
/// SetProperty, RemoveProperty, ToDictionary, AsList, GetList) used inside scripts
/// to work with Instance.Data dynamic values.
/// </summary>
[Collection("ScriptingTests")]
public class DynamicHelperFunctionsTests : ApplicationTestBase<ApplicationEntryPoint>
{
    private readonly IScriptEngine _scriptEngine;

    public DynamicHelperFunctionsTests()
    {
        _scriptEngine = GetRequiredService<IScriptEngine>();
    }

    protected override void AddApplication(IServiceCollection services)
    {
        var mockDaprClient = new Mock<DaprClient>();
        mockDaprClient
            .Setup(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        services.AddSingleton(mockDaprClient.Object);

        var mockLogger = new Mock<ILogger<ScriptServices>>();
        services.AddSingleton(mockLogger.Object);

        var mockConfiguration = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        services.AddSingleton(mockConfiguration.Object);

        var mockMetrics = new Mock<IWorkflowMetrics>();
        services.AddSingleton(mockMetrics.Object);

        base.AddApplication(services);
    }

    /// <summary>
    /// Compiles a script body and returns its mapping instance along with a minimal context.
    /// Test data is created inside the script body using CreateList/CreateObject/SetProperty helpers.
    /// </summary>
    private async Task<(IMapping mapping, ScriptContext context)> BuildScriptAsync(string scriptBody)
    {
        var code = $$"""
                     using System;
                     using System.Collections.Generic;
                     using System.Threading.Tasks;
                     using BBT.Workflow.Scripting;
                     using BBT.Workflow.Definitions;
                     using BBT.Workflow.Scripting.Functions;

                     public class TestMapping : ScriptBase, IMapping
                     {
                         public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                         {
                             {{scriptBody}}
                         }

                         public Task<ScriptResponse> OutputHandler(ScriptContext context)
                             => Task.FromResult(new ScriptResponse { Data = null });
                     }
                     """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location)
        };

        var usings = new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Threading.Tasks",
            "BBT.Workflow.Scripting",
            "BBT.Workflow.Definitions",
            "BBT.Workflow.Scripting.Functions"
        };

        var mapping = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references, usings);

        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetWorkflow(WorkflowFactory.CreateDefault())
            .SetInstance(InstanceFactory.CreateDefault())
            .SetTransition(TransitionFactory.CreateDefault())
            .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
            .SetDefinitions(new Dictionary<string, object>())
            .Build();

        return (mapping, context);
    }

    // ── Helper: builds a List<object?> with 3 items (id: 1/2/3, status: active/inactive/active)
    // Used inline inside script bodies via CreateList/CreateObject/SetProperty/ListAdd helpers.
    private const string ThreeItemListScript = """
        var items = CreateList();
        dynamic i1 = CreateObject(); SetProperty(i1, "id", "1"); SetProperty(i1, "status", "active");   ListAdd(items, i1);
        dynamic i2 = CreateObject(); SetProperty(i2, "id", "2"); SetProperty(i2, "status", "inactive"); ListAdd(items, i2);
        dynamic i3 = CreateObject(); SetProperty(i3, "id", "3"); SetProperty(i3, "status", "active");   ListAdd(items, i3);
        """;

    #region AsList / GetList

    [Fact]
    public async Task AsList_Should_Return_Empty_List_For_Null()
    {
        var (mapping, context) = await BuildScriptAsync(
            """
            var list = AsList(null);
            return Task.FromResult(new ScriptResponse { Data = list.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(0, (int)response.Data);
    }

    [Fact]
    public async Task AsList_Should_Return_Same_List_When_Already_List()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            var original = CreateList();
            ListAdd(original, "x");
            var casted = AsList(original);
            return Task.FromResult(new ScriptResponse { Data = casted.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(1, (int)response.Data);
    }

    [Fact]
    public async Task GetList_Should_Return_Named_List_Property()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            dynamic data = CreateObject();
            SetProperty(data, "items", items);
            var result = GetList(data, "items");
            return Task.FromResult(new ScriptResponse { Data = result.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(3, (int)response.Data);
    }

    [Fact]
    public async Task GetList_Should_Return_Empty_List_For_Missing_Property()
    {
        var (mapping, context) = await BuildScriptAsync(
            """
            dynamic data = CreateObject();
            var result = GetList(data, "nonExistent");
            return Task.FromResult(new ScriptResponse { Data = result.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(0, (int)response.Data);
    }

    #endregion

    #region ListFilter

    [Fact]
    public async Task ListFilter_Should_Return_Only_Matching_Items()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var active = ListFilter(items, x => (string)x.status == "active");
            return Task.FromResult(new ScriptResponse { Data = active.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(2, (int)response.Data);
    }

    [Fact]
    public async Task ListFilter_Should_Return_Empty_List_When_No_Match()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var deleted = ListFilter(items, x => (string)x.status == "deleted");
            return Task.FromResult(new ScriptResponse { Data = deleted.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(0, (int)response.Data);
    }

    #endregion

    #region ListFirst / ListLast

    [Fact]
    public async Task ListFirst_Without_Predicate_Should_Return_First_Item()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var first = ListFirst(items);
            return Task.FromResult(new ScriptResponse { Data = (string)first.id });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal("1", (string)response.Data);
    }

    [Fact]
    public async Task ListFirst_With_Predicate_Should_Return_First_Matching_Item()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var first = ListFirst(items, x => (string)x.status == "inactive");
            return Task.FromResult(new ScriptResponse { Data = (string)first.id });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal("2", (string)response.Data);
    }

    [Fact]
    public async Task ListFirst_Should_Return_Null_When_No_Match()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var item = ListFirst(items, x => (string)x.status == "deleted");
            return Task.FromResult(new ScriptResponse { Data = item == null });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.True((bool)response.Data);
    }

    [Fact]
    public async Task ListLast_With_Predicate_Should_Return_Last_Matching_Item()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var last = ListLast(items, x => (string)x.status == "active");
            return Task.FromResult(new ScriptResponse { Data = (string)last.id });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal("3", (string)response.Data);
    }

    #endregion

    #region ListAny / ListCount

    [Fact]
    public async Task ListAny_Without_Predicate_Should_Return_True_When_List_Has_Items()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var hasItems = ListAny(items);
            return Task.FromResult(new ScriptResponse { Data = hasItems });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.True((bool)response.Data);
    }

    [Fact]
    public async Task ListAny_With_Predicate_Should_Return_False_When_No_Match()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var hasDeleted = ListAny(items, x => (string)x.status == "deleted");
            return Task.FromResult(new ScriptResponse { Data = hasDeleted });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.False((bool)response.Data);
    }

    [Fact]
    public async Task ListCount_Without_Predicate_Should_Return_Total_Count()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var count = ListCount(items);
            return Task.FromResult(new ScriptResponse { Data = count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(3, (int)response.Data);
    }

    [Fact]
    public async Task ListCount_With_Predicate_Should_Return_Filtered_Count()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var count = ListCount(items, x => (string)x.status == "active");
            return Task.FromResult(new ScriptResponse { Data = count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(2, (int)response.Data);
    }

    #endregion

    #region ListSelect

    [Fact]
    public async Task ListSelect_Should_Project_Property_From_Each_Item()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var ids = ListSelect<string>(items, x => (string)x.id);
            return Task.FromResult(new ScriptResponse { Data = string.Join(",", ids) });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal("1,2,3", (string)response.Data);
    }

    #endregion

    #region ListAdd / ListRemove

    [Fact]
    public async Task ListAdd_Should_Increase_List_Count()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            dynamic newItem = CreateObject();
            SetProperty(newItem, "id", "4");
            SetProperty(newItem, "status", "active");
            ListAdd(items, newItem);
            return Task.FromResult(new ScriptResponse { Data = items.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(4, (int)response.Data);
    }

    [Fact]
    public async Task ListRemove_Should_Remove_Matching_Items_And_Return_Removed_Count()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            var removed = ListRemove(items, x => (string)x.status == "inactive");
            return Task.FromResult(new ScriptResponse { Data = removed });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(1, (int)response.Data);
    }

    [Fact]
    public async Task ListRemove_Should_Leave_Non_Matching_Items_In_List()
    {
        var (mapping, context) = await BuildScriptAsync(
            $$"""
            {{ThreeItemListScript}}
            ListRemove(items, x => (string)x.status == "inactive");
            return Task.FromResult(new ScriptResponse { Data = items.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(2, (int)response.Data);
    }

    #endregion

    #region CreateObject / CreateList / SetProperty / RemoveProperty / ToDictionary

    [Fact]
    public async Task CreateObject_And_SetProperty_Should_Allow_Reading_Back_Value()
    {
        var (mapping, context) = await BuildScriptAsync(
            """
            dynamic obj = CreateObject();
            SetProperty(obj, "name", "test-value");
            return Task.FromResult(new ScriptResponse { Data = (string)obj.name });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal("test-value", (string)response.Data);
    }

    [Fact]
    public async Task CreateList_Should_Return_New_Empty_List()
    {
        var (mapping, context) = await BuildScriptAsync(
            """
            var list = CreateList();
            return Task.FromResult(new ScriptResponse { Data = list.Count });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.Equal(0, (int)response.Data);
    }

    [Fact]
    public async Task RemoveProperty_Should_Remove_Existing_Property_And_Return_True()
    {
        var (mapping, context) = await BuildScriptAsync(
            """
            dynamic obj = CreateObject();
            SetProperty(obj, "tempField", "temporary");
            SetProperty(obj, "name", "keep");
            var removed = RemoveProperty(obj, "tempField");
            return Task.FromResult(new ScriptResponse { Data = removed });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.True((bool)response.Data);
    }

    [Fact]
    public async Task RemoveProperty_Should_Return_False_For_Nonexistent_Property()
    {
        var (mapping, context) = await BuildScriptAsync(
            """
            dynamic obj = CreateObject();
            SetProperty(obj, "name", "test");
            var removed = RemoveProperty(obj, "nonExistent");
            return Task.FromResult(new ScriptResponse { Data = removed });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.False((bool)response.Data);
    }

    [Fact]
    public async Task ToDictionary_Should_Contain_All_Set_Properties()
    {
        var (mapping, context) = await BuildScriptAsync(
            """
            dynamic obj = CreateObject();
            SetProperty(obj, "name", "test");
            SetProperty(obj, "value", 42);
            var dict = ToDictionary(obj);
            return Task.FromResult(new ScriptResponse { Data = dict.ContainsKey("name") && dict.ContainsKey("value") });
            """);

        var response = await mapping.InputHandler(WorkflowTaskFactory.CreateHttpTask(), context);

        Assert.True((bool)response.Data);
    }

    #endregion
}
