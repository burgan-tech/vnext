using System;
using System.IO;
using BBT.Workflow.Scripting.Sandbox;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

public class SandboxScriptAssemblyCatalogTests
{
    private static SandboxScriptAssemblyCatalog CatalogWithPlugins(string pluginDirectory) =>
        new(new ScriptSandboxOptions { PluginDirectory = pluginDirectory });

    [Theory]
    [InlineData("System.Text.RegularExpressions")]
    [InlineData("system.text.regularexpressions")]
    public void IsAvailable_ReturnsTrue_ForFrameworkAssembly(string name)
    {
        CatalogWithPlugins(string.Empty).IsAvailable(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Definitely.Not.An.Assembly")]
    [InlineData("System.Text.RegularExpressions.dll")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAvailable_ReturnsFalse_ForUnresolvableName(string name)
    {
        CatalogWithPlugins(string.Empty).IsAvailable(name).ShouldBeFalse();
    }

    [Fact]
    public void IsAvailable_ReturnsTrue_ForDllInPluginDirectory()
    {
        // A unique directory per test: the plugin listing is cached per directory for the process lifetime.
        var dir = Path.Combine(Path.GetTempPath(), "vnext-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Acme.Plugin.dll"), []);

            var catalog = CatalogWithPlugins(dir);

            catalog.IsAvailable("Acme.Plugin").ShouldBeTrue();
            catalog.IsAvailable("Acme.Other").ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
