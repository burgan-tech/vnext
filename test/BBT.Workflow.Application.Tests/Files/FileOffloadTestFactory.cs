using System;
using System.Threading;
using System.Threading.Tasks;

using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Files;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BBT.Workflow.Application.Files;

internal static class FileOffloadTestFactory
{
    public static (FileOffloadService Service, Definitions.Workflow Workflow) Create(
        string masterSchemaJson, IFileBlobStore store, string domain, string flow, FileStorageOptions? options = null)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(flow, domain, "sys-flows", "1.0.0"));
        workflow.SetType("F");
        workflow.SetSchema(new Reference("master", domain, "sys-schemas", "1.0.0"));

        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            $$"""{ "type": "JSON", "schema": {{masterSchemaJson}} }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference("master", domain, "sys-schemas", "1.0.0"));

        var cache = Substitute.For<IComponentCacheStore>();
        cache.GetSchemaAsync(domain, "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(domain);

        return (new FileOffloadService(cache, store, runtime,
            Microsoft.Extensions.Options.Options.Create(options ?? new FileStorageOptions()), NullLogger<FileOffloadService>.Instance), workflow);
    }
}
