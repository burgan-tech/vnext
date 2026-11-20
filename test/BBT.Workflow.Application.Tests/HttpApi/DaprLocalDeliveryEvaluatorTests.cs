using BBT.Workflow.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>Unit tests for <see cref="DaprLocalDeliveryEvaluator"/>.</summary>
public class DaprLocalDeliveryEvaluatorTests
{
    private const string Secret = "s3cret";

    private static HeaderDictionary Headers(
        StringValues? token = null, string? caller = null, string tokenName = DaprLocalDeliveryEvaluator.TokenHeader)
    {
        var headers = new HeaderDictionary();
        if (token is { } t)
        {
            headers[tokenName] = t;
        }

        if (caller is not null)
        {
            headers[DaprLocalDeliveryEvaluator.CallerAppIdHeader] = caller;
        }

        return headers;
    }

    [Fact]
    public void Evaluate_MatchingTokenNoCaller_Accepted()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: Secret), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.Accepted);

    [Fact]
    public void Evaluate_NoToken_TokenMissing()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.TokenMissing);

    [Fact]
    public void Evaluate_WrongToken_TokenInvalid()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: "wrong"), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.TokenInvalid);

    [Fact]
    public void Evaluate_TwoTokenValues_TokenRepeated()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: new StringValues(new[] { Secret, Secret })), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.TokenRepeated);

    [Fact]
    public void Evaluate_CommaJoinedSingleValue_TokenInvalid()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: "s3cret, s3cret"), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.TokenInvalid);

    [Fact]
    public void Evaluate_TokenWithSurroundingSpace_TokenInvalid()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: " s3cret "), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.TokenInvalid);

    [Fact]
    public void Evaluate_CallerHeaderPresent_Rejected()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: Secret, caller: "vnext-x-app"), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.CallerHeaderPresent);

    [Fact]
    public void Evaluate_EmptyCallerHeaderValue_StillRejected()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: Secret, caller: ""), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.CallerHeaderPresent);

    [Fact]
    public void Evaluate_HeaderNameIsCaseInsensitive_Accepted()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: Secret, tokenName: "Dapr-Api-Token"), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.Accepted);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Evaluate_TokenNotConfigured_RejectsEvenWithHeader(string? expected)
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: "x"), expected)
            .ShouldBe(DaprLocalDeliveryVerdict.TokenNotConfigured);

    [Fact]
    public void Evaluate_CallerHeaderCheckedBeforeToken()
        => DaprLocalDeliveryEvaluator.Evaluate(Headers(token: "wrong", caller: "a"), Secret)
            .ShouldBe(DaprLocalDeliveryVerdict.CallerHeaderPresent);

    [Fact]
    public void Options_DefaultToLogOnly()
    {
        var options = new DaprLocalDeliveryOptions();
        options.Mode.ShouldBe(DaprLocalDeliveryMode.LogOnly);
        DaprLocalDeliveryOptions.SectionPath.ShouldBe("Dapr:LocalDelivery");
    }
}
