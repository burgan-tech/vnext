using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Execution;
using BBT.Workflow.Files;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// I4 (x-storage final review): what the file admission costs a transition on a flow whose master schema declares NO
/// <c>x-storage</c> field — the hot path every existing flow now pays. The master schema comes through a component
/// cache stand-in that behaves like the bytes-mode L1 hit (<see cref="ComponentL1Cache"/>: the envelope is
/// deserialized from UTF-8 bytes on every call), so the numbers include that deserialize, then the content-keyed
/// <c>GetFields</c> memo hit. No database or binding I/O.
/// <list type="bullet">
///   <item><see cref="Baseline_NoOp"/> — an admission that does nothing (the cost before the feature)</item>
///   <item><see cref="GetFields_MemoHit"/> — the parse+memo path on an already-resolved schema (no deserialize)</item>
///   <item><see cref="GetFieldsAsync_L1Deserialize"/> — schema load through the L1-style cache + memo hit</item>
///   <item><see cref="Admission_FlowWithoutXStorage"/> — the full <see cref="FileAdmission.ApplyAsync"/> call; the
///   payload carries no <c>content</c>/<c>file</c> member, so the prefilter skips the schema load (the common case)</item>
///   <item><see cref="Admission_FlowWithoutXStorage_20KbPayload"/> — same with a ~20 KB body (prefilter walk cost)</item>
///   <item><see cref="Admission_FlowWithoutXStorage_FileShapedPayload"/> — a <c>file</c> member defeats the prefilter: schema load + memo</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class FileAdmissionBenchmarks
{
    private const string Domain = "bench";

    private FileOffloadService _service = null!;
    private FileAdmission _admission = null!;
    private Definitions.Workflow _workflow = null!;
    private SchemaDefinition _resolvedSchema = null!;
    private TransitionExecutionContext _context = null!;
    private WorkflowExecutionContext _workflowContext = null!;
    private TransitionExecutionContext _largeContext = null!;
    private WorkflowExecutionContext _largeWorkflowContext = null!;
    private TransitionExecutionContext _fileShapedContext = null!;
    private WorkflowExecutionContext _fileShapedWorkflowContext = null!;

    /// <summary>Approximate size of the master schema (no x-storage field anywhere).</summary>
    [Params(5, 20)]
    public int SchemaKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var schemaJson = MasterSchemaWithoutXStorage(SchemaKb);
        _resolvedSchema = Deserialize(schemaJson);

        var l1 = new ComponentL1Cache(
            Options.Create(new ComponentCacheOptions()), TimeProvider.System, NullLogger<ComponentL1Cache>.Instance);
        l1.Set("schema:bench:master:1.0.0",
            new CacheEnvelope<SchemaDefinition> { Domain = Domain, Key = "master", Version = "1.0.0", Entity = _resolvedSchema },
            DateTimeOffset.UtcNow.AddHours(1));
        var cache = new L1SchemaCacheStore(l1, "schema:bench:master:1.0.0");

        _service = new FileOffloadService(cache, new UnusedBlobStore(), new BenchRuntimeInfo(),
            Options.Create(new FileStorageOptions()), NullLogger<FileOffloadService>.Instance);
        _admission = new FileAdmission(_service, new NoopRawBody());

        _workflow = Definitions.Workflow.Create();
        _workflow.SetReference(new Reference("flow", Domain, "sys-flows", "1.0.0"));
        _workflow.SetType("F");
        _workflow.SetSchema(new Reference("master", Domain, "sys-schemas", "1.0.0"));

        (_context, _workflowContext) = Contexts(
            JsonDocument.Parse("""{ "customer": { "name": "n", "segment": "retail" }, "amount": 12.5 }""").RootElement.Clone());
        (_largeContext, _largeWorkflowContext) = Contexts(JsonDocument.Parse(PayloadFactory.Json(20)).RootElement.Clone());
        // A "file" member somewhere (not at an x-storage path): the prefilter cannot skip, the schema is loaded.
        (_fileShapedContext, _fileShapedWorkflowContext) = Contexts(
            JsonDocument.Parse("""{ "customer": { "name": "n" }, "attachment": { "file": "not-x-storage" } }""").RootElement.Clone());

        // Prime the memo so every measured call is the steady-state hit.
        if (_service.GetFields(_resolvedSchema).Count != 0)
            throw new InvalidOperationException("the benchmark schema must not declare x-storage");
    }

    private (TransitionExecutionContext, WorkflowExecutionContext) Contexts(JsonElement payload)
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0");
        var context = new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = Domain,
            WorkflowKey = "flow",
            TransitionKey = "submit",
            Trigger = TriggerType.Manual,
            CorrelationId = "c",
            ExecutionChainId = "e",
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = _workflow,
            Transition = Transition.Create("submit", "s1", "s1", TriggerType.Manual, "Patch"),
            Instance = instance,
            Data = payload,
            TraceId = "t",
            SpanId = "s"
        };
        var workflowContext = new WorkflowExecutionContext
        {
            InstanceId = instance.Id.ToString(),
            Domain = Domain,
            WorkflowKey = "flow",
            TransitionKey = "submit",
            Data = new TransitionDataInfo(payload)
        };
        return (context, workflowContext);
    }

    [Benchmark(Baseline = true)]
    public Task<Result> Baseline_NoOp() => Task.FromResult(Result.Ok());

    [Benchmark]
    public int GetFields_MemoHit() => _service.GetFields(_resolvedSchema).Count;

    [Benchmark]
    public async Task<int> GetFieldsAsync_L1Deserialize()
        => (await _service.GetFieldsAsync(_workflow, CancellationToken.None)).Value!.Count;

    [Benchmark]
    public Task<Result> Admission_FlowWithoutXStorage()
        => _admission.ApplyAsync(_context, _workflowContext, CancellationToken.None);

    /// <summary>Same, with a ~20 KB request body: the prefilter walks the whole payload.</summary>
    [Benchmark]
    public Task<Result> Admission_FlowWithoutXStorage_20KbPayload()
        => _admission.ApplyAsync(_largeContext, _largeWorkflowContext, CancellationToken.None);

    /// <summary>A payload with a <c>file</c> member: the prefilter cannot skip and the schema is loaded (worst case).</summary>
    [Benchmark]
    public Task<Result> Admission_FlowWithoutXStorage_FileShapedPayload()
        => _admission.ApplyAsync(_fileShapedContext, _fileShapedWorkflowContext, CancellationToken.None);

    /// <summary>A master schema of ~<paramref name="approxKb"/> KB: typed properties, nested objects, arrays, x-roles.</summary>
    private static string MasterSchemaWithoutXStorage(int approxKb)
    {
        var sb = new StringBuilder(approxKb * 1024 + 256);
        sb.Append("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{");
        var i = 0;
        while (sb.Length < approxKb * 1024)
        {
            if (i > 0) sb.Append(',');
            sb.Append("\"field").Append(i).Append("\":{\"type\":\"object\",\"x-labels\":{\"en\":\"Field ").Append(i)
              .Append("\",\"tr\":\"Alan ").Append(i).Append("\"},\"x-roles\":[{\"role\":\"officer\",\"grant\":\"allow\"}],")
              .Append("\"properties\":{\"code\":{\"type\":\"string\",\"maxLength\":32},\"amount\":{\"type\":\"number\",\"minimum\":0},")
              .Append("\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"sku\":{\"type\":\"string\"}}}}}}");
            i++;
        }
        sb.Append("}}");
        return sb.ToString();
    }

    private static SchemaDefinition Deserialize(string schemaJson)
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            $$"""{ "type": "JSON", "schema": {{schemaJson}} }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference("master", Domain, "sys-schemas", "1.0.0"));
        return schema;
    }

    /// <summary>Serves the master schema the way an L1 hit does: deserialized from the cached bytes on every call.</summary>
    private sealed class L1SchemaCacheStore(IComponentL1Cache l1, string cacheKey) : IComponentCacheStore
    {
        public Task<Result<SchemaDefinition>> GetSchemaAsync(string domain, string key, string? version,
            CancellationToken cancellationToken = default)
            => Task.FromResult(l1.TryGet<SchemaDefinition>(cacheKey)?.Entity is { } schema
                ? Result<SchemaDefinition>.Ok(schema)
                : Result<SchemaDefinition>.Fail(Error.NotFound("bench:miss", "L1 miss")));

        public Task<Result<Definitions.Workflow>> GetFlowAsync(string domain, string key, string? version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<WorkflowTask>> GetTaskAsync(string domain, string key, string? version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<Function>> GetFunctionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<View>> GetViewAsync(string domain, string key, string? version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<Extension>> GetExtensionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<IEnumerable<Extension>>> GetAllExtensionsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<Mapping>> GetMappingAsync(string domain, string key, string? version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> SetAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class, IDomainEntity, IReferenceSetter => throw new NotSupportedException();
    }

    private sealed class UnusedBlobStore : IFileBlobStore
    {
        public Task<Result> PutAsync(string component, string file, ReadOnlyMemory<byte> bytes, string? mimeType, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<Result<byte[]>> GetAsync(string component, string file, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NoopRawBody : IRequestRawBodyProvider
    {
        public string? GetRawBody() => null;
    }

    private sealed class BenchRuntimeInfo : IRuntimeInfoProvider
    {
        public string Domain => FileAdmissionBenchmarks.Domain;
        public string Version => "bench";
        public void Check(string requestDomain) { }
        public bool IsDomainMatch(string? requestDomain) => true;
    }
}
