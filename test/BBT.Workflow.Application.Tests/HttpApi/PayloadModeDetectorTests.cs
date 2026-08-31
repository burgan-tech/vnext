using System.Text.Json;
using BBT.Workflow.Orchestration.Controllers.Instances;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Unit tests for <see cref="PayloadModeDetector"/>: it decides whether a request body is a
/// standard vNext envelope (<c>key</c> / <c>tags</c> / <c>stage</c> / <c>attributes</c>) or a
/// free-form business payload that must be wrapped under <c>attributes</c>.
/// <para>
/// The distinction is load-bearing for schema validation: a misclassified standard envelope is
/// wrapped whole, so the transition/start schema is evaluated against <c>key</c>/<c>tags</c>
/// instead of the business payload and rejects a perfectly valid request.
/// </para>
/// </summary>
public class PayloadModeDetectorTests
{
    private static JsonElement? Body(string json) => JsonDocument.Parse(json).RootElement;

    private static IHeaderDictionary Headers(string? mode = null)
    {
        var headers = new HeaderDictionary();
        if (mode is not null)
            headers["x-vnext-payload-mode"] = mode;
        return headers;
    }

    // ---------------------------------------------------------------- header override

    [Theory]
    [InlineData("standard", true)]
    [InlineData("STANDARD", true)]
    [InlineData("raw", false)]
    [InlineData("RAW", false)]
    public void Header_overrides_body_shape(string mode, bool expected)
        => PayloadModeDetector.IsStandard(Headers(mode), Body("""{"amount":1}""")).ShouldBe(expected);

    // ---------------------------------------------------------------- attributes present

    [Theory]
    [InlineData("""{"attributes":{"amount":1}}""")]
    [InlineData("""{"key":"K1","attributes":{"amount":1}}""")]
    [InlineData("""{"key":"K1","tags":["a"],"stage":"S","attributes":{"amount":1}}""")]
    // An unrecognized sibling must not demote a body that clearly carries the envelope.
    [InlineData("""{"attributes":{"amount":1},"foo":1}""")]
    public void Body_with_attributes_is_standard(string json)
        => PayloadModeDetector.IsStandard(Headers(), Body(json)).ShouldBeTrue();

    /// <summary>
    /// JSON binding downstream is case-insensitive (<see cref="JsonSerializerOptions.Web"/>), so
    /// detection must be too — otherwise <c>Attributes</c> binds fine but is never reached.
    /// </summary>
    [Theory]
    [InlineData("""{"Attributes":{"amount":1}}""")]
    [InlineData("""{"ATTRIBUTES":{"amount":1}}""")]
    public void Attributes_matching_is_case_insensitive(string json)
        => PayloadModeDetector.IsStandard(Headers(), Body(json)).ShouldBeTrue();

    // ---------------------------------------------------------------- envelope without attributes

    /// <summary>
    /// The envelope fields are all optional. A standard payload that carries only <c>key</c> /
    /// <c>tags</c> / <c>stage</c> — i.e. "no business data" — is still a standard payload and
    /// must not be wrapped into <c>attributes</c>.
    /// </summary>
    [Theory]
    [InlineData("""{"key":"K1"}""")]
    [InlineData("""{"tags":["a"]}""")]
    [InlineData("""{"stage":"Initial"}""")]
    [InlineData("""{"key":"K1","tags":["a"],"stage":"Initial"}""")]
    [InlineData("""{"Key":"K1","Tags":["a"]}""")]
    public void Envelope_only_body_is_standard(string json)
        => PayloadModeDetector.IsStandard(Headers(), Body(json)).ShouldBeTrue();

    // ---------------------------------------------------------------- free-form

    [Theory]
    [InlineData("""{"amount":1,"currency":"TRY"}""")]
    [InlineData("""{"key":"K1","amount":1}""")]
    [InlineData("""{"contractCode":"CT-1","sub":"u"}""")]
    public void Business_payload_is_free_form(string json)
        => PayloadModeDetector.IsStandard(Headers(), Body(json)).ShouldBeFalse();

    /// <summary>
    /// An empty object carries no signal either way. It stays free-form so it normalizes to an
    /// empty <c>attributes</c> object — the long-standing shape for "start with no data".
    /// </summary>
    [Fact]
    public void Empty_object_stays_free_form()
        => PayloadModeDetector.IsStandard(Headers(), Body("{}")).ShouldBeFalse();

    [Fact]
    public void Null_body_is_standard()
        => PayloadModeDetector.IsStandard(Headers(), null).ShouldBeTrue();

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void Non_object_body_is_standard(string json)
        => PayloadModeDetector.IsStandard(Headers(), Body(json)).ShouldBeTrue();
}
