using System;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Tasks;

/// <summary>
/// The multipart parts a mapping script builds on an <see cref="HttpTask"/>: order, defaults, validation,
/// and that cloning, pooling and reset carry or clear them like every other HTTP property.
/// </summary>
public class HttpTaskMultipartTests
{
    [Fact]
    public void AddMultipart_KeepsThePartsInOrder_WithFileDefaults()
    {
        var task = HttpTask.CreateEmpty();
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0xE2, 0xE3, 0xCF, 0xD3 };

        task.AddMultipartFile("file", bytes);
        task.AddMultipartField("note", "çek");
        task.AddMultipartFile("scan", bytes, "cek.pdf", "application/pdf");

        task.Multipart.ShouldNotBeNull();
        task.Multipart!.Count.ShouldBe(3);
        task.Multipart[0].ShouldBe(new HttpMultipartPart
        {
            Name = "file", Content = bytes, FileName = "file", ContentType = "application/octet-stream"
        });
        task.Multipart[1].Name.ShouldBe("note");
        task.Multipart[1].Value.ShouldBe("çek");
        task.Multipart[1].Content.ShouldBeNull();
        task.Multipart[2].FileName.ShouldBe("cek.pdf");
        task.Multipart[2].ContentType.ShouldBe("application/pdf");
        task.Multipart[2].Content.ShouldBeSameAs(bytes);
    }

    [Fact]
    public void AddMultipartField_NullValue_IsAnEmptyField()
    {
        var task = HttpTask.CreateEmpty();

        task.AddMultipartField("pairs", null);

        task.Multipart![0].Value.ShouldBe(string.Empty);
    }

    [Fact]
    public void AddMultipart_RefusesAMissingNameOrContent()
    {
        var task = HttpTask.CreateEmpty();

        Should.Throw<ArgumentException>(() => task.AddMultipartField(" ", "x"));
        Should.Throw<ArgumentException>(() => task.AddMultipartFile("", new byte[] { 1 }));
        Should.Throw<ArgumentNullException>(() => task.AddMultipartFile("file", null!));
        Should.Throw<ArgumentException>(() => task.AddMultipartFile("file", new byte[] { 1 }, "a.pdf", "not a type"));
        task.Multipart.ShouldBeNull();
    }

    [Fact]
    public void ClearMultipart_DropsEveryPart()
    {
        var task = HttpTask.CreateEmpty();
        task.AddMultipartField("a", "1");

        task.ClearMultipart();

        task.Multipart.ShouldBeNull();
    }

    [Fact]
    public void CloneTyped_CarriesTheParts_AndAPartAddedToTheCloneStaysOnTheClone()
    {
        var task = HttpTask.CreateEmpty();
        task.SetReference(new Reference("http-task", "test-domain", "sys-tasks", "1.0.0"));
        task.AddMultipartField("a", "1");

        var clone = task.CloneTyped();
        clone.AddMultipartField("b", "2");

        clone.Multipart!.Count.ShouldBe(2);
        task.Multipart!.Count.ShouldBe(1);
    }

    [Fact]
    public void CopyFromInternal_CarriesTheParts()
    {
        var source = HttpTask.CreateEmpty();
        source.AddMultipartFile("file", new byte[] { 1, 2, 3 }, "x.bin");
        var target = HttpTask.CreateEmpty();

        target.CopyFromInternal(source);

        target.Multipart.ShouldBe(source.Multipart);
    }

    [Fact]
    public void Reset_ClearsTheParts()
    {
        var task = HttpTask.CreateEmpty();
        task.AddMultipartField("a", "1");

        task.Reset();

        task.Multipart.ShouldBeNull();
    }
}
