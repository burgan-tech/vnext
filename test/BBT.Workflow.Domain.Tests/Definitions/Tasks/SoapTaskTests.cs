using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Definitions.Tasks;

public class SoapTaskTests
{
    private static string MakeConfig(
        string url = "http://example.com/service.asmx",
        string soapAction = "http://example.com/GetData",
        string soapVersion = "1.1",
        string? body = null,
        int timeoutSeconds = 30,
        bool validateSsl = true,
        string[]? acceptedStatusCodes = null)
    {
        var obj = new Dictionary<string, object>
        {
            ["url"] = url,
            ["soapAction"] = soapAction,
            ["soapVersion"] = soapVersion,
            ["timeoutSeconds"] = timeoutSeconds,
            ["validateSsl"] = validateSsl
        };
        if (body != null) obj["body"] = body;
        if (acceptedStatusCodes != null) obj["acceptedStatusCodes"] = acceptedStatusCodes;
        return JsonSerializer.Serialize(obj);
    }

    [Fact]
    public void Create_ShouldParseAllPropertiesFromJson()
    {
        var config = MakeConfig(
            url: "http://example.com/svc.asmx",
            soapAction: "http://example.com/Op",
            soapVersion: "1.2",
            body: "<soap:Envelope/>",
            timeoutSeconds: 60,
            validateSsl: false,
            acceptedStatusCodes: ["500"]);

        var task = SoapTask.Create(config.ToJsonElement());

        Assert.Equal("http://example.com/svc.asmx", task.Url);
        Assert.Equal("http://example.com/Op", task.SoapAction);
        Assert.Equal("1.2", task.SoapVersion);
        Assert.Equal("<soap:Envelope/>", task.Body);
        Assert.Equal(60, task.TimeoutSeconds);
        Assert.False(task.ValidateSSL);
        Assert.NotNull(task.AcceptedStatusCodes);
        Assert.Contains("500", task.AcceptedStatusCodes);
    }

    [Fact]
    public void Create_Defaults_ShouldBeSet()
    {
        var config = MakeConfig();
        var task = SoapTask.Create(config.ToJsonElement());

        Assert.Equal("1.1", task.SoapVersion);
        Assert.Equal(30, task.TimeoutSeconds);
        Assert.True(task.ValidateSSL);
        Assert.Null(task.AcceptedStatusCodes);
    }

    [Fact]
    public void Type_ShouldBe16()
    {
        var task = SoapTask.Create(MakeConfig().ToJsonElement());
        Assert.Equal("16", task.Type);
    }

    [Fact]
    public void CloneTyped_ShouldProduceDeepCopy()
    {
        var config = MakeConfig(body: "<Envelope/>", soapVersion: "1.2");
        var original = SoapTask.Create(config.ToJsonElement());
        original.AddHeader("Authorization", "Basic xxx");

        var clone = original.CloneTyped();

        Assert.Equal(original.Url, clone.Url);
        Assert.Equal(original.SoapAction, clone.SoapAction);
        Assert.Equal(original.SoapVersion, clone.SoapVersion);
        Assert.Equal(original.Body, clone.Body);
        Assert.Equal(original.TimeoutSeconds, clone.TimeoutSeconds);
        Assert.Equal(original.ValidateSSL, clone.ValidateSSL);

        // Modifying clone headers must not affect original
        clone.AddHeader("X-Extra", "value");
        var originalHeaders = GetHeadersDictionary(original);
        Assert.False(originalHeaders.ContainsKey("X-Extra"));
    }

    [Fact]
    public void AddHeader_WhenHeadersNull_ShouldAddNewHeader()
    {
        var task = SoapTask.CreateEmpty();

        task.AddHeader("Authorization", "Bearer token");

        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.Equal("Bearer token", headers["Authorization"]);
    }

    [Fact]
    public void AddHeader_WhenKeyExists_ShouldOverrideValue()
    {
        var task = SoapTask.CreateEmpty();
        task.SetHeaders(new Dictionary<string, string?> { ["Authorization"] = "old" });

        task.AddHeader("Authorization", "new");

        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.Equal("new", headers["Authorization"]);
    }

    [Fact]
    public void RemoveHeader_WhenKeyExists_ShouldRemoveIt()
    {
        var task = SoapTask.CreateEmpty();
        task.SetHeaders(new Dictionary<string, string?> { ["X-A"] = "a", ["X-B"] = "b" });

        task.RemoveHeader("X-A");

        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.False(headers.ContainsKey("X-A"));
    }

    [Fact]
    public void RemoveHeader_WhenKeyDoesNotExist_ShouldDoNothing()
    {
        var task = SoapTask.CreateEmpty();
        task.SetHeaders(new Dictionary<string, string?> { ["X-A"] = "a" });

        task.RemoveHeader("X-Missing");

        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
    }

    [Fact]
    public void Reset_ShouldRestoreDefaults()
    {
        var task = SoapTask.Create(MakeConfig(
            url: "http://example.com",
            soapAction: "",
            soapVersion: "1.2",
            timeoutSeconds: 60,
            validateSsl: false).ToJsonElement());

        task.Reset();

        Assert.Equal(string.Empty, task.Url);
        Assert.Equal(string.Empty, task.SoapAction);
        Assert.Equal("1.1", task.SoapVersion);
        Assert.Null(task.Body);
        Assert.Null(task.Headers);
        Assert.Equal(30, task.TimeoutSeconds);
        Assert.True(task.ValidateSSL);
        Assert.Null(task.AcceptedStatusCodes);
    }

    [Fact]
    public void AcceptedStatusCodes_WithWildcardPattern_ShouldParse()
    {
        var config = MakeConfig(acceptedStatusCodes: ["5xx", "404"]);
        var task = SoapTask.Create(config.ToJsonElement());

        Assert.NotNull(task.AcceptedStatusCodes);
        Assert.Contains("5xx", task.AcceptedStatusCodes);
        Assert.Contains("404", task.AcceptedStatusCodes);
    }

    [Fact]
    public void SetBody_ShouldUpdateBody()
    {
        var task = SoapTask.CreateEmpty();
        const string xml = "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body/></soap:Envelope>";

        task.SetBody(xml);

        Assert.Equal(xml, task.Body);
    }

    private static Dictionary<string, string?> GetHeadersDictionary(SoapTask task)
    {
        if (!task.Headers.HasValue)
            return new Dictionary<string, string?>();
        var json = task.Headers.Value.GetRawText();
        return JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
               ?? new Dictionary<string, string?>();
    }
}
