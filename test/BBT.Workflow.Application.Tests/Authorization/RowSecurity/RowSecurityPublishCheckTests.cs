using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Definitions.Schemas;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Schema- and key-aware publish checks for <c>attributes.rowSecurity</c> (RS-E08, E14, E15, E16, E18 and
/// RS-W0, W1, W2a, W2c, W5, W6r, W7) run by <see cref="RowSecurityPublishCheck"/>.
/// </summary>
public sealed class RowSecurityPublishCheckTests
{
    private const string SchemaKey = "rs-schema";

    private static readonly string[] SchemaFields = ["secret", "tckn", "iban", "protected", "region", "branchCode"];

    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly ILogger<RowSecurityPublishCheck> _logger = Substitute.For<ILogger<RowSecurityPublishCheck>>();

    public RowSecurityPublishCheckTests()
    {
        // [LoggerMessage] methods check IsEnabled before calling Log.
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
    }

    private const string WorkflowShell = """
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "schema": {"key": "rs-schema", "domain": "test-domain", "flow": "sys-schemas", "version": "1.0.0"},
            "states": [
                {
                    "key": "review",
                    "stateType": 2,
                    "versionStrategy": "Minor",
                    "labels": [{"label": "Review", "language": "en"}],
                    "transitions": []
                }
                __EXTRA_STATE__
            ],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start",
                "target": "review",
                "versionStrategy": "Minor",
                "triggerType": 0,
                "labels": [{"label": "Start", "language": "en"}]
            },
            "rowSecurity": __ROW_SECURITY__
        }
        """;

    private const string SubFlowState = """
        ,{
            "key": "waiting",
            "stateType": "subflow",
            "labels": [{"label": "Waiting", "language": "en"}],
            "subFlow": {
                "type": "S",
                "process": {"key": "sub", "domain": "test-domain", "flow": "sys-flows", "version": "1.0.0"},
                "mapping": { "location": "./src/X.csx", "code": "cmV0dXJuIHRydWU7" }
            }
        }
        """;

    private static string Rule(string filter) => $$"""{"rules":[{"roles":["officer"],"filter":{{filter}}}]}""";

    private static string LeafOn(string field, string op = "eq", string value = "\"x\"") =>
        Rule("{\"attributes\":{\"" + field + "\":{\"" + op + "\":" + value + "}}}");

    private static string QueryLeafOn(string field) => LeafOn(field, "eq", "\"$.context.QueryParameters.p\"");
    private static string HeaderLeafOn(string field) => LeafOn(field, "eq", "\"$.context.Headers.x-branch-code\"");
    private static string InLeafOn(string field) => LeafOn(field, "in", "[\"a\",\"b\"]");
    private static string EqLeafOn(string field) => LeafOn(field, "eq", "\"$.context.Headers.x-branch-code\"");

    private static readonly string Def = LeafOn("branchCode");

    private static WorkflowDefinition WorkflowWith(string rowSecurityJson, bool withSubFlow = false)
    {
        var json = WorkflowShell
            .Replace("__ROW_SECURITY__", rowSecurityJson)
            .Replace("__EXTRA_STATE__", withSubFlow ? SubFlowState : "");
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
    }

    private static SchemaDefinition SchemaWith(
        string? encrypt = null,
        string? hash = null,
        string? mask = null,
        string? xRoles = null,
        string[]? indexed = null,
        string[]? required = null,
        string? extraProperties = null)
    {
        indexed ??= [];
        required ??= [];
        var properties = SchemaFields.Select(field =>
        {
            var members = new List<string> { "\"type\":\"string\"" };
            if (field == encrypt) members.Add("\"x-encryption\":{\"type\":\"encrypt\"}");
            if (field == hash) members.Add("\"x-encryption\":{\"type\":\"hash\"}");
            if (field == mask) members.Add("\"x-masking\":{\"operator\":\"mask\"}");
            if (field == xRoles) members.Add("\"x-roles\":[{\"role\":\"officer\",\"grant\":\"allow\"}]");
            if (indexed.Contains(field)) members.Add("\"x-indexed\":true");
            return $"\"{field}\":{{{string.Join(",", members)}}}";
        }).ToList();
        if (extraProperties is not null)
            properties.Add(extraProperties);

        var schema = $$"""
            { "type": "object", "required": [{{string.Join(",", required.Select(r => $"\"{r}\""))}}],
              "properties": { {{string.Join(",", properties)}} } }
            """;
        var definition = JsonSerializer.Deserialize<SchemaDefinition>(
            $$"""{ "type": "master", "schema": {{schema}} }""", JsonSerializerConstants.JsonOptions)!;
        definition.SetReference(new Reference(SchemaKey, "test-domain", "sys-schemas", "1.0.0"));
        return definition;
    }

    private static readonly SchemaDefinition? SchemaMissing = null;

    private async Task<IReadOnlyList<RowSecurityFinding>> Check(
        string componentKey, string rowSecurityJson, SchemaDefinition? schema, bool withSubFlow = false)
    {
        _cache.GetSchemaAsync(Arg.Any<string>(), SchemaKey, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(schema is null
                ? Result<SchemaDefinition>.Fail(Error.NotFound("Cache:300001", "Schema not found"))
                : Result<SchemaDefinition>.Ok(schema));

        var check = new RowSecurityPublishCheck(_cache, _logger);
        return await check.CheckAsync(componentKey, WorkflowWith(rowSecurityJson, withSubFlow), CancellationToken.None);
    }

    private Task<IReadOnlyList<RowSecurityFinding>> Check(string componentKey, string rowSecurityJson) =>
        Check(componentKey, rowSecurityJson, SchemaWith());

    private static string[] Codes(IEnumerable<RowSecurityFinding> findings) => findings.Select(f => f.Code).ToArray();

    [Fact]
    public async Task SysFlow_IsRejected_E16()
    {
        var findings = await Check("sys-views", Def);
        findings.Select(f => f.Code).ShouldContain("RS-E16");
        findings.Single(f => f.Code == "RS-E16").Severity.ShouldBe(RowSecurityFindingSeverity.Error);
    }

    [Fact]
    public async Task LeafOnEncryptedPath_E08()
        => Codes(await Check("rs-a", LeafOn("secret"), SchemaWith(encrypt: "secret"))).ShouldContain("RS-E08");

    [Fact]
    public async Task LeafUnderEncryptedAncestor_E08()
        => Codes(await Check("rs-a", LeafOn("secret.inner"), SchemaWith(encrypt: "secret"))).ShouldContain("RS-E08");

    [Fact]
    public async Task LeafOnAncestorOfEncryptedPath_E08()
    {
        var schema = SchemaWith(extraProperties:
            "\"card\":{\"type\":\"object\",\"properties\":{\"pan\":{\"type\":\"string\",\"x-encryption\":{\"type\":\"encrypt\"}}}}");
        Codes(await Check("rs-a", LeafOn("card", "includes", "{\"pan\":\"1\"}"), schema)).ShouldContain("RS-E08");
    }

    [Fact]
    public async Task LeafOnHashedPath_E14()
        => Codes(await Check("rs-a", LeafOn("tckn"), SchemaWith(hash: "tckn"))).ShouldContain("RS-E14");

    [Fact]
    public async Task LeafOnMaskedPath_E15()
        => Codes(await Check("rs-a", LeafOn("iban"), SchemaWith(mask: "iban"))).ShouldContain("RS-E15");

    [Fact]
    public async Task QueryParameterLeafOnRolesPath_E18()
        => Codes(await Check("rs-a", QueryLeafOn("protected"), SchemaWith(xRoles: "protected"))).ShouldContain("RS-E18");

    [Fact]
    public async Task LiteralLeafOnRolesPath_IsAccepted()
        => (await Check("rs-a", LeafOn("protected"), SchemaWith(xRoles: "protected"))).ShouldBeEmpty();

    [Fact]
    public async Task HeaderLeafOnRolesPath_WarnsW6r()
    {
        var finding = (await Check("rs-a", HeaderLeafOn("protected"), SchemaWith(xRoles: "protected"))).Single();
        finding.Code.ShouldBe("RS-W6r");
        finding.Severity.ShouldBe(RowSecurityFindingSeverity.Warning);
        finding.Path.ShouldBe("rowSecurity.rules[0].filter.protected.eq");
    }

    [Fact]
    public async Task InOnUnindexedField_WarnsW2a()
    {
        var findings = await Check("rs-a", InLeafOn("region"), SchemaWith(indexed: []));
        findings.ShouldContain(f => f.Code == "RS-W2a" && f.Severity == RowSecurityFindingSeverity.Warning);
    }

    [Fact]
    public async Task InOnIndexedField_DoesNotWarn()
        => (await Check("rs-a", InLeafOn("region"), SchemaWith(indexed: ["region"]))).ShouldBeEmpty();

    [Theory]
    [InlineData("gt", "\"1\"")]
    [InlineData("between", "[\"1\",\"2\"]")]
    [InlineData("startswith", "\"A\"")]
    public async Task RangeAndPatternOnUnindexedField_WarnW2a(string op, string value)
        => Codes(await Check("rs-a", LeafOn("region", op, value), SchemaWith())).ShouldContain("RS-W2a");

    [Fact]
    public async Task EqOnUnindexedField_DoesNotWarn()
        => (await Check("rs-a", EqLeafOn("branchCode"), SchemaWith(indexed: []))).ShouldBeEmpty();

    [Fact]
    public async Task SchemaMissing_DowngradesToW0_AndLogs()
    {
        var finding = (await Check("rs-a", Def, SchemaMissing)).Single();
        finding.Code.ShouldBe("RS-W0");
        finding.Severity.ShouldBe(RowSecurityFindingSeverity.Warning);
        _logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log) &&
                                           ((EventId)c.GetArguments()[1]!).Id == 20510).ShouldBe(1);
    }

    [Fact]
    public async Task SchemaMissing_NoProtectedPathError()
        => Codes(await Check("rs-a", LeafOn("iban"), SchemaMissing)).ShouldBe(["RS-W0"]);

    [Fact]
    public async Task FieldNotInSchema_WarnsW1()
        => Codes(await Check("rs-a", LeafOn("unknownField"))).ShouldContain("RS-W1");

    [Fact]
    public async Task InstanceColumn_IsNotSchemaChecked()
        => (await Check("rs-a", LeafOn("status", "eq", "\"A\""))).ShouldBeEmpty();

    [Fact]
    public async Task NeAsOnlyPredicate_WarnsW2c()
        => Codes(await Check("rs-a", LeafOn("branchCode", "ne", "\"x\""), SchemaWith(required: ["branchCode"])))
            .ShouldBe(["RS-W2c"]);

    [Fact]
    public async Task NeBesideEq_DoesNotWarnW2c()
    {
        var rule = Rule("""{"and":[{"attributes":{"branchCode":{"ne":"x"}}},{"attributes":{"region":{"eq":"y"}}}]}""");
        Codes(await Check("rs-a", rule, SchemaWith(required: ["branchCode"]))).ShouldNotContain("RS-W2c");
    }

    [Fact]
    public async Task NotOnOptionalField_WarnsW5()
    {
        var rule = Rule("""{"and":[{"not":{"attributes":{"branchCode":{"eq":"x"}}}},{"attributes":{"region":{"eq":"y"}}}]}""");
        Codes(await Check("rs-a", rule)).ShouldBe(["RS-W5"]);
    }

    [Fact]
    public async Task NotOnRequiredField_DoesNotWarnW5()
    {
        var rule = Rule("""{"and":[{"not":{"attributes":{"branchCode":{"eq":"x"}}}},{"attributes":{"region":{"eq":"y"}}}]}""");
        (await Check("rs-a", rule, SchemaWith(required: ["branchCode"]))).ShouldBeEmpty();
    }

    [Fact]
    public async Task NinOnOptionalField_WarnsW5()
        => Codes(await Check("rs-a", LeafOn("branchCode", "nin", "[\"x\"]"))).ShouldContain("RS-W5");

    [Fact]
    public async Task SubFlowState_WarnsW7_AndLogs()
    {
        var findings = await Check("rs-a", Def, SchemaWith(), withSubFlow: true);
        Codes(findings).ShouldBe(["RS-W7"]);
        _logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log) &&
                                           ((EventId)c.GetArguments()[1]!).Id == 20511).ShouldBe(1);
    }

    [Fact]
    public async Task RuleWithoutFilter_DoesNotReadSchema()
    {
        (await Check("rs-a", """{"rules":[{"roles":["officer"]}]}""", SchemaMissing)).ShouldBeEmpty();
        await _cache.DidNotReceiveWithAnyArgs().GetSchemaAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task FindingsNeverEchoAuthoredValues()
    {
        var findings = await Check("rs-a", LeafOn("iban", "eq", "\"CANARY-7f3a\""), SchemaWith(mask: "iban"));
        findings.ShouldAllBe(f => !f.Message.Contains("CANARY-7f3a"));
    }

    [Theory]
    [InlineData("secret", null, RowSecurityPathProtection.Encrypted)]
    [InlineData("secret.inner", null, RowSecurityPathProtection.Encrypted)]
    [InlineData("tckn", null, RowSecurityPathProtection.Hashed)]
    [InlineData("iban", RowParameterSource.CurrentUser, RowSecurityPathProtection.Masked)]
    [InlineData("protected", RowParameterSource.QueryParameters, RowSecurityPathProtection.QueryOnRoleRestricted)]
    [InlineData("protected", RowParameterSource.Headers, RowSecurityPathProtection.HeaderOnRoleRestricted)]
    [InlineData("protected", RowParameterSource.CurrentUser, RowSecurityPathProtection.None)]
    [InlineData("protected", null, RowSecurityPathProtection.None)]
    [InlineData("region", RowParameterSource.QueryParameters, RowSecurityPathProtection.None)]
    public void Classify_ReturnsProtection(string field, RowParameterSource? source, RowSecurityPathProtection expected)
    {
        var schema = SchemaWith(encrypt: "secret", hash: "tckn", mask: "iban", xRoles: "protected");
        var exposure = SchemaRolesParser.ParseExposure(schema.Schema);
        RowSecurityProtectedPaths.Classify(field, exposure, source).ShouldBe(expected);
    }

    [Theory]
    [InlineData(RowSecurityPathProtection.Encrypted, true, "RS-E08")]
    [InlineData(RowSecurityPathProtection.Hashed, true, "RS-E14")]
    [InlineData(RowSecurityPathProtection.Masked, true, "RS-E15")]
    [InlineData(RowSecurityPathProtection.QueryOnRoleRestricted, true, "RS-E18")]
    [InlineData(RowSecurityPathProtection.HeaderOnRoleRestricted, false, "RS-W6r")]
    [InlineData(RowSecurityPathProtection.None, false, null)]
    public void Protection_MapsToRefusalAndCode(RowSecurityPathProtection protection, bool refused, string? code)
    {
        RowSecurityProtectedPaths.IsRefused(protection).ShouldBe(refused);
        RowSecurityProtectedPaths.CodeOf(protection).ShouldBe(code);
    }
}
