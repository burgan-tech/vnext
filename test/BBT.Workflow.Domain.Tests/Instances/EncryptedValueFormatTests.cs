using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The pre-admission check of a request body: a client may send back the value already stored at the same path (a token
/// or a digest) and nothing else carrying a reserved prefix. Runs before the pipeline so the refusal is a 400 and never a
/// faulted instance.
/// </summary>
public sealed class EncryptedValueFormatTests
{
    private const string StoredToken = "ENCRYPTED:AES256:i1:AAAA";
    private const string StoredDigest = "HASHED:SHA256:abcd";

    private static readonly JsonElement Stored =
        JsonDocument.Parse($$$"""{"vault":{"email":"{{{StoredToken}}}","tckn":"{{{StoredDigest}}}"}}""").RootElement;

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AnEchoOfTheStoredToken_IsAllowed()
        => EncryptedValueFormat.FindIntroducedToken(Body($$$"""{"vault":{"email":"{{{StoredToken}}}"}}"""), Stored).ShouldBeNull();

    [Fact]
    public void AnEchoOfTheStoredDigest_IsAllowed()
        => EncryptedValueFormat.FindIntroducedToken(Body($$$"""{"vault":{"tckn":"{{{StoredDigest}}}"}}"""), Stored).ShouldBeNull();

    [Fact]
    public void ATokenAtAnotherPath_IsReported()
        => EncryptedValueFormat.FindIntroducedToken(Body($$$"""{"vault":{"label":"{{{StoredToken}}}"}}"""), Stored).ShouldBe("vault.label");

    [Fact]
    public void AnotherDigestAtTheSamePath_IsReported()
        => EncryptedValueFormat.FindIntroducedToken(Body("""{"vault":{"tckn":"HASHED:SHA256:ffff"}}"""), Stored).ShouldBe("vault.tckn");

    [Fact]
    public void AnyPrefixOnANewInstance_IsReported()
        => EncryptedValueFormat.FindIntroducedToken(Body($$$"""{"vault":{"email":"{{{StoredToken}}}"}}"""), null).ShouldBe("vault.email");

    [Fact]
    public void APrefixInsideAnArray_IsReported()
        => EncryptedValueFormat.FindIntroducedToken(Body("""{"tags":["x","HASHED:SHA256:z"]}"""), Stored).ShouldBe("tags[]");

    [Fact]
    public void ABodyWithoutThePrefixes_IsClean()
    {
        var body = Body("""{"vault":{"email":"user@example.com"},"note":"ENCRYPTED and HASHED are just words"}""");

        EncryptedValueFormat.MayContainReserved(body.GetRawText()).ShouldBeFalse();
        EncryptedValueFormat.FindIntroducedToken(body, Stored).ShouldBeNull();
    }
}
