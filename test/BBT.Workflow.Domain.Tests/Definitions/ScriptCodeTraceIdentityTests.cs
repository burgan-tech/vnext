using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

/// <summary>
/// Pins the identity that names the <c>Script.Compile</c> span.
/// <para>
/// Without it, a transition evaluating three auto-transition rules produced three spans all called
/// <c>Script.Compile</c> — one of them costing 1.5 s — with no way to tell which rule was the
/// expensive one. The identity has to be readable (a path, not a hash) whenever the definition
/// gives us one, which in practice is nearly always: 208 of 209 script blocks in vnext-example
/// carry a real <c>location</c>.
/// </para>
/// </summary>
public sealed class ScriptCodeTraceIdentityTests
{
    [Fact]
    public void Location_WhenAuthored_IsTheIdentity()
    {
        var script = ScriptCode.FromNative("return true;", location: "./src/AlwaysTrueRule.csx");

        script.TraceIdentity.ShouldBe("./src/AlwaysTrueRule.csx");
    }

    [Fact]
    public void InlineLocation_FallsBackToAContentHashPrefix()
    {
        // "inline" is the DEFAULT, i.e. the author gave us nothing — it identifies no script at
        // all, so it must not become the span name. A hash prefix at least separates two different
        // inline scripts from each other.
        var script = ScriptCode.FromNative("return true;");

        script.Location.ShouldBe(ScriptCode.DefaultLocation);
        script.TraceIdentity.ShouldStartWith("inline:");
        script.TraceIdentity.Length.ShouldBeGreaterThan("inline:".Length);
    }

    [Fact]
    public void TwoDifferentInlineScripts_GetDifferentIdentities()
    {
        var a = ScriptCode.FromNative("return true;");
        var b = ScriptCode.FromNative("return false;");

        a.TraceIdentity.ShouldNotBe(b.TraceIdentity);
    }

    [Fact]
    public void ReferenceEncoded_WithoutLocation_UsesTheReference()
    {
        // A reference-encoded script's DecodedCode is empty, so EVERY such script shares the
        // empty-string ContentHash. The reference is the only thing that identifies it.
        var reference = new Reference("shared-rule", "core", "sys-mappings", "1.0.0");
        var script = ScriptCode.FromReference(reference);

        script.TraceIdentity.ShouldBe("core/sys-mappings/shared-rule/1.0.0");
    }

    [Fact]
    public void ReferenceEncoded_WithLocation_PrefersTheLocation()
    {
        var reference = new Reference("shared-rule", "core", "sys-mappings", "1.0.0");
        var script = ScriptCode.FromReference(reference, location: "./src/SharedRule.csx");

        script.TraceIdentity.ShouldBe("./src/SharedRule.csx");
    }
}
