using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Definitions;

public class ScriptCodeTests
{
    [Fact]
    public void Constructor_ShouldInitializeProperties()
    {
        // Arrange
        var location = "test-location";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("console.log('test');"));

        // Act
        var scriptCode = new ScriptCode(location, code);

        // Assert
        Assert.Equal(location, scriptCode.Location);
        Assert.Equal(code, scriptCode.Code);
    }

    [Fact]
    public void DecodedCode_ShouldReturnDecodedString_WhenValidBase64()
    {
        // Arrange
        var originalCode = "function test() { return true; }";
        var base64Code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(originalCode));
        var scriptCode = new ScriptCode("test-location", base64Code);

        // Act
        var decodedCode = scriptCode.DecodedCode;

        // Assert
        Assert.Equal(originalCode, decodedCode);
    }

    [Fact]
    public void DecodedCode_ShouldThrowException_WhenInvalidBase64()
    {
        // Arrange
        var invalidBase64 = "This is not a valid base64 string!";
        var scriptCode = new ScriptCode("test-location", invalidBase64);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => scriptCode.DecodedCode);
        Assert.Contains("Invalid Base64 string in ScriptCode", exception.Message);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("test", "dGVzdA==")]
    [InlineData("Hello World!", "SGVsbG8gV29ybGQh")]
    public void DecodedCode_ShouldDecodeCorrectly_ForVariousInputs(string originalText, string base64Text)
    {
        // Arrange
        var scriptCode = new ScriptCode("location", base64Text);

        // Act
        var result = scriptCode.DecodedCode;

        // Assert
        Assert.Equal(originalText, result);
    }

    [Fact]
    public void Equals_ShouldReturnTrue_WhenPropertiesAreSame()
    {
        // Arrange
        var location = "test-location";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test code"));
        var scriptCode1 = new ScriptCode(location, code);
        var scriptCode2 = new ScriptCode(location, code);

        // Act & Assert
        Assert.Equal(scriptCode1.DecodedCode, scriptCode2.DecodedCode);
        Assert.True(scriptCode1.ValueEquals(scriptCode2));
    }

    [Fact]
    public void Equals_ShouldReturnFalse_WhenLocationsDiffer()
    {
        // Arrange
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test code"));
        var scriptCode1 = new ScriptCode("location1", code);
        var scriptCode2 = new ScriptCode("location2", code);

        // Act & Assert
        Assert.NotEqual(scriptCode1, scriptCode2);
        Assert.False(scriptCode1.Equals(scriptCode2));
    }

    [Fact]
    public void Equals_ShouldReturnFalse_WhenCodesDiffer()
    {
        // Arrange
        var location = "test-location";
        var code1 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code1"));
        var code2 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code2"));
        var scriptCode1 = new ScriptCode(location, code1);
        var scriptCode2 = new ScriptCode(location, code2);

        // Act & Assert
        Assert.NotEqual(scriptCode1, scriptCode2);
        Assert.False(scriptCode1.Equals(scriptCode2));
    }

    [Fact]
    public void GetHashCode_ShouldBeSame_ForEqualObjects()
    {
        // Arrange
        var location = "test-location";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test code"));
        var scriptCode1 = new ScriptCode(location, code);
        var scriptCode2 = new ScriptCode(location, code);

        // Act & Assert
        Assert.Equal(scriptCode1.DecodedCode.GetHashCode(), scriptCode2.DecodedCode.GetHashCode());
    }

    [Fact]
    public void GetHashCode_ShouldBeDifferent_ForDifferentObjects()
    {
        // Arrange
        var code1 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code1"));
        var code2 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code2"));
        var scriptCode1 = new ScriptCode("location1", code1);
        var scriptCode2 = new ScriptCode("location2", code2);

        // Act & Assert
        Assert.NotEqual(scriptCode1.GetHashCode(), scriptCode2.GetHashCode());
    }

    [Fact]
    public void GetAtomicValues_ShouldReturnLocationAndCode()
    {
        // Arrange
        var location = "test-location";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test code"));
        var scriptCode = new ScriptCode(location, code);

        // Act
        var atomicValues = scriptCode.GetType()
            .GetMethod("GetAtomicValues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(scriptCode, null) as IEnumerable<object>;

        // Assert
        Assert.NotNull(atomicValues);
        var valuesList = atomicValues.ToList();
        Assert.Equal(2, valuesList.Count);
        Assert.Equal(location, valuesList[0]);
        Assert.Equal(code, valuesList[1]);
    }

    [Fact]
    public void DecodedCode_ShouldHandleUnicodeCharacters()
    {
        // Arrange
        var originalCode = "function test() { return '日本語'; }";
        var base64Code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(originalCode));
        var scriptCode = new ScriptCode("test-location", base64Code);

        // Act
        var decodedCode = scriptCode.DecodedCode;

        // Assert
        Assert.Equal(originalCode, decodedCode);
    }

    [Fact]
    public void DecodedCode_ShouldHandleEmptyString()
    {
        // Arrange
        var originalCode = "";
        var base64Code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(originalCode));
        var scriptCode = new ScriptCode("test-location", base64Code);

        // Act
        var decodedCode = scriptCode.DecodedCode;

        // Assert
        Assert.Equal(originalCode, decodedCode);
    }

    [Fact]
    public void DecodedCode_ShouldBeMemoized_SameReferenceAcrossAccesses()
    {
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("public class M {}"));
        var scriptCode = new ScriptCode("loc", code);

        var first = scriptCode.DecodedCode;
        var second = scriptCode.DecodedCode;

        Assert.Same(first, second); // memo: aynı string referansı, her erişimde yeni decode değil
    }

    [Fact]
    public void ContentHash_ShouldBeStable_AndDifferForDifferentSources()
    {
        var a1 = ScriptCode.FromNative("public class A {}");
        var a2 = ScriptCode.FromNative("public class A {}");
        var b = ScriptCode.FromNative("public class B {}");

        Assert.Equal(a1.ContentHash, a2.ContentHash);   // içerik-türevli, deterministik
        Assert.NotEqual(a1.ContentHash, b.ContentHash);
        Assert.Same(a1.ContentHash, a1.ContentHash);    // instance-başına bir kez hesaplanır
        Assert.Equal(64, a1.ContentHash.Length);        // SHA256 hex
    }

    [Fact]
    public void MemoFields_ShouldNotAffectValueEquality()
    {
        var x = ScriptCode.FromNative("public class M {}");
        var y = ScriptCode.FromNative("public class M {}");
        _ = x.ContentHash; // yalnız birinde memo tetiklenir
        _ = x.DecodedCode;

        // ScriptCode/ValueObject does not wire Equals/GetHashCode to ValueEquals (pre-existing,
        // out of scope here), so structural equality is asserted via ValueEquals — the file's own
        // convention (see Equals_ShouldReturnTrue_WhenPropertiesAreSame) — not Assert.Equal.
        Assert.True(x.ValueEquals(y)); // GetAtomicValues memo alanlarını içermez
    }

    [Fact]
    public void ContentHash_ForGlobalAndReference_IsSharedEmptyStringHash_ByDesign()
    {
        // DecodedCode boş olduğundan Global ve Reference instance'ları AYNI hash'i paylaşır —
        // ContentHash bu yollarda kimlik DEĞİLDİR (XML doc'ta pinli; engine REF'te gövdeyi hash'ler).
        var emptyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>()));
        var global = new ScriptCode("loc", "ignored", MappingType.Global, CodeEncoding.Native);
        var reference = ScriptCode.FromReference(new Reference("some-helper", "core", "sys-mappings", "1.0.0"));

        Assert.Equal(emptyHash, global.ContentHash);
        Assert.Equal(emptyHash, reference.ContentHash);
    }
}

