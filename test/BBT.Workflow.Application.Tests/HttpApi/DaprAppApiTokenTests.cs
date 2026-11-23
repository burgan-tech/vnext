using System;
using System.IO;
using BBT.Workflow.Middlewares;
using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>The Dapr app API token is read once from a mounted file and never echoed.</summary>
public sealed class DaprAppApiTokenTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"vnext-apitoken-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_file))
            File.Delete(_file);
    }

    [Fact]
    public void Load_ReturnsNullForBlankPath()
    {
        DaprAppApiTokenLoader.Load(null).ShouldBeNull();
        DaprAppApiTokenLoader.Load("  ").ShouldBeNull();
    }

    [Fact]
    public void Load_TrimsTrailingNewline()
    {
        File.WriteAllText(_file, "abc123\n");

        DaprAppApiTokenLoader.Load(_file).ShouldBe("abc123");
    }

    [Fact]
    public void Load_ThrowsOnMissingFile()
        => Should.Throw<InvalidOperationException>(() => DaprAppApiTokenLoader.Load(_file))
            .Message.ShouldContain(_file);

    [Fact]
    public void Load_ThrowsOnEmptyFile()
    {
        File.WriteAllText(_file, " \n");

        var message = Should.Throw<InvalidOperationException>(() => DaprAppApiTokenLoader.Load(_file)).Message;

        message.ShouldContain(_file);
        message.ShouldContain("empty");
    }

    [Fact]
    public void Load_MessageNeverContainsFileContent()
    {
        // A directory path fails to read (UnauthorizedAccessException / IOException); the message names
        // the path and the exception type only.
        var directory = Path.Combine(Path.GetTempPath(), $"vnext-apitoken-dir-{Guid.NewGuid():N}-planted-s3cret");
        Directory.CreateDirectory(directory);
        try
        {
            var message = Should.Throw<InvalidOperationException>(() => DaprAppApiTokenLoader.Load(directory)).Message;

            message.ShouldContain(directory);
            message.ShouldMatch("(UnauthorizedAccessException|IOException)");
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Value_DoesNotChangeWhenFileIsRewritten()
    {
        File.WriteAllText(_file, "first");
        var token = new DaprAppApiToken(Options.Create(new RuntimeCallerOptions { AppApiTokenFile = _file }));

        File.WriteAllText(_file, "second");

        token.Value.ShouldBe("first");
        token.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public void Unset_IsNotConfigured()
    {
        var token = new DaprAppApiToken(Options.Create(new RuntimeCallerOptions()));

        token.Value.ShouldBeNull();
        token.IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public void ToString_DoesNotRevealValue()
    {
        var text = new DaprAppApiToken("s3cret").ToString();

        text.ShouldNotContain("s3cret");
        text.ShouldBe("DaprAppApiToken(configured)");
        new DaprAppApiToken((string?)null).ToString().ShouldBe("DaprAppApiToken(not configured)");
    }
}
