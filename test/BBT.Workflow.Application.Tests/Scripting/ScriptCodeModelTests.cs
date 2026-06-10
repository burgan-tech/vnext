using System;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Tests for the Extension-2 model changes: the <c>scripts</c> object, <see cref="ScriptSettings.Union"/>,
/// and the polymorphic <c>code</c> field (string vs <see cref="Reference"/> for <c>REF</c> encoding) via
/// <see cref="ScriptCodeJsonConverter"/>.
/// </summary>
public class ScriptCodeModelTests
{
    private static readonly JsonSerializerOptions Options = JsonSerializerConstants.JsonOptions;

    [Fact]
    public void ScriptSettings_Union_Dedupes_Helpers_And_Merges_Grants()
    {
        var flow = new ScriptSettings(
            new[] { new Reference("rsa-crypto", "core", "sys-mappings", "1.0.0") },
            new[] { "System.Security.Cryptography" });

        var task = new ScriptSettings(
            new[]
            {
                new Reference("rsa-crypto", "core", "sys-mappings", "1.0.0"), // duplicate of flow's
                new Reference("json-helper", "core", "sys-mappings", "1.0.0")
            },
            new[] { "System.Text.Json", "system.security.cryptography" }); // last is a case-variant dup

        var union = ScriptSettings.Union(flow, task);

        union.ShouldNotBeNull();
        union!.Helpers!.Count.ShouldBe(2); // rsa-crypto (deduped) + json-helper
        union.Helpers!.Select(h => h.Key).ShouldBe(new[] { "rsa-crypto", "json-helper" });
        union.AllowedAssemblies!.Count.ShouldBe(2); // crypto (deduped, case-insensitive) + json
    }

    [Fact]
    public void ScriptSettings_Union_Returns_NonNull_Side_When_Other_Null()
    {
        var task = new ScriptSettings(new[] { new Reference("h", "core", "sys-mappings", "1.0.0") }, null);

        ScriptSettings.Union(null, task).ShouldBeSameAs(task);
        ScriptSettings.Union(task, null).ShouldBeSameAs(task);
        ScriptSettings.Union(null, null).ShouldBeNull();
    }

    [Fact]
    public void ScriptCode_Json_Reads_Scripts_Object_For_Native_Code()
    {
        const string json = """
        {
          "location": "./src/UserSessionMapping.csx",
          "code": "public class M {}",
          "encoding": "NAT",
          "scripts": {
            "helpers": [ { "key": "rsa-crypto", "version": "1.0.0", "domain": "core", "flow": "sys-mappings" } ],
            "allowedAssemblies": [ "System.Security.Cryptography" ]
          }
        }
        """;

        var sc = JsonSerializer.Deserialize<ScriptCode>(json, Options);

        sc.ShouldNotBeNull();
        sc!.Encoding.ShouldBe(CodeEncoding.Native);
        sc.IsReference.ShouldBeFalse();
        sc.DecodedCode.ShouldBe("public class M {}");
        sc.HasMappingCode.ShouldBeTrue();
        sc.HasHelpers.ShouldBeTrue();
        sc.Scripts!.Helpers!.Single().Key.ShouldBe("rsa-crypto");
        sc.Scripts.AllowedAssemblies!.ShouldContain("System.Security.Cryptography");
    }

    [Fact]
    public void ScriptCode_Json_Reads_Reference_Code_For_Ref_Encoding()
    {
        const string json = """
        {
          "code": { "key": "json-helper", "version": "1.0.0", "domain": "core", "flow": "sys-mappings" },
          "encoding": "REF"
        }
        """;

        var sc = JsonSerializer.Deserialize<ScriptCode>(json, Options);

        sc.ShouldNotBeNull();
        sc!.IsReference.ShouldBeTrue();
        sc.CodeReference.ShouldNotBeNull();
        sc.CodeReference!.Key.ShouldBe("json-helper");
        sc.CodeReference.Flow.ShouldBe("sys-mappings");
        sc.HasMappingCode.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => _ = sc.DecodedCode);
    }

    [Fact]
    public void ScriptCode_Json_RoundTrips_Ref_Encoding()
    {
        var original = ScriptCode.FromReference(new Reference("json-helper", "core", "sys-mappings", "1.0.0"));

        var json = JsonSerializer.Serialize(original, Options);
        var clone = JsonSerializer.Deserialize<ScriptCode>(json, Options);

        clone.ShouldNotBeNull();
        clone!.IsReference.ShouldBeTrue();
        clone.CodeReference!.ToString().ShouldBe("core/sys-mappings/json-helper/1.0.0");
    }
}
