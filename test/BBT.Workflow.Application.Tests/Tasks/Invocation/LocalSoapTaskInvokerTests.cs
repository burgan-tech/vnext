using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Invocation.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The in-process SOAP invoker must produce exactly what the Execution service's
/// <c>SoapTaskInvoker</c> produces for the same binding, including the <see cref="TaskTypes.Soap"/>
/// result label — output mapping scripts and the InstanceTasks journal must not be able to tell
/// which host made the call.
/// </summary>
public sealed class LocalSoapTaskInvokerTests
{
    /// <summary>
    /// The current <c>SoapTaskInvoker</c>/<c>SoapInvocation</c> success test is
    /// <c>response.IsSuccessStatusCode || AcceptedStatusCodeMatcher.IsAccepted(...)</c> — it does
    /// NOT consult <c>IsSoapFault</c>. A Fault delivered over HTTP 200 is therefore reported as a
    /// SUCCESSFUL result today (a pre-existing trait of the shared body, not something this task
    /// introduces). A Fault is only surfaced as a failed result when it arrives on a non-2xx status,
    /// which is the documented common case (<see cref="SoapTaskBinding.AcceptedStatusCodes"/>:
    /// "SOAP faults typically come back over HTTP 500"). This test uses 500 to exercise that real
    /// path rather than asserting behaviour the code does not have.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_SoapFaultOverNonSuccessStatus_IsReportedAsFailure()
    {
        const string fault = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><soap:Fault><faultstring>bad request</faultstring></soap:Fault></soap:Body>
            </soap:Envelope>
            """;
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(fault)
        });
        var invoker = new LocalSoapTaskInvoker(
            new CapturingHttpClientFactory(handler), NullLogger<LocalSoapTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("legacy-call", Binding(), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
        result.Metadata!["IsSoapFault"].ShouldBe(true);
        result.ErrorMessage.ShouldContain("bad request");
        result.TaskType.ShouldBe(TaskTypes.Soap);
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulResponse_ReturnsResultWithSoapVersionMetadata()
    {
        const string body = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><Result>ok</Result></soap:Body>
            </soap:Envelope>
            """;
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        });
        var invoker = new LocalSoapTaskInvoker(
            new CapturingHttpClientFactory(handler), NullLogger<LocalSoapTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("legacy-call", Binding(), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["SoapVersion"].ShouldBe("1.1");
        result.TaskType.ShouldBe(TaskTypes.Soap);
    }

    [Fact]
    public void TaskType_IsTheWireSoapConstant()
    {
        var invoker = new LocalSoapTaskInvoker(
            new CapturingHttpClientFactory(new StubHttpMessageHandler(new HttpResponseMessage())),
            NullLogger<LocalSoapTaskInvoker>.Instance);

        invoker.TaskType.ShouldBe(TaskTypes.Soap);
    }

    [Fact]
    public async Task InvokeAsync_EmptyBinding_FailsWithTheSoapTaskTypeLabel()
    {
        var invoker = new LocalSoapTaskInvoker(
            new CapturingHttpClientFactory(new StubHttpMessageHandler(new HttpResponseMessage())),
            NullLogger<LocalSoapTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync(
            "legacy-call", JsonSerializer.SerializeToElement((object?)null), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
        result.TaskType.ShouldBe(TaskTypes.Soap);
    }

    private static JsonElement Binding() => JsonSerializer.SerializeToElement(new SoapTaskBinding
    {
        Url = "https://legacy.local/service",
        SoapVersion = "1.1",
        Body = "<x/>"
    });
}
