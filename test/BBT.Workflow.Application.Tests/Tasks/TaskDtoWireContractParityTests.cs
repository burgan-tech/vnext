using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks;

/// <summary>
/// Pins the structural equivalence of the two DTO families that carry the same task-invocation
/// wire contract across the Orchestration/Execution boundary: <c>BBT.Workflow.Tasks.*</c> (the
/// client side, defined in <c>BBT.Workflow.Tasks.Abstractions</c>) and
/// <c>BBT.Workflow.Execution.*</c> (the server side, defined in
/// <c>BBT.Workflow.Execution.Abstractions</c>).
///
/// The two families are bridged ONLY by JSON being structural (property name + JSON-shape) on
/// both the HTTP transport and the gRPC proxy-mode transport's JSON payload envelope -- there is
/// no shared base type or contract interface enforcing them to agree. A property added to one
/// side and not mirrored on the other silently drops on the wire on BOTH transports: the sender
/// serializes a field the receiver's type has no matching property for, so deserialization just
/// leaves it out, with no error anywhere.
///
/// If this test fails, the fix is almost always to add/rename the missing property on the OTHER
/// side of the pair named in the failure message -- not to change the test's expectations.
/// </summary>
public sealed class TaskDtoWireContractParityTests
{
    /// <summary>
    /// The paired DTO type names. Each must exist as a public sealed type in both
    /// <c>BBT.Workflow.Tasks</c> (client) and <c>BBT.Workflow.Execution</c> (server).
    /// </summary>
    private static readonly string[] PairedTypeNames =
    [
        "TaskEnvelope",
        "TaskTraceContext",
        "TaskInvokeRequest",
        "TaskInvokeResponse",
        "TaskInvocationResult",
    ];

    public static IEnumerable<object[]> PairedTypeNamesData() =>
        PairedTypeNames.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(PairedTypeNamesData))]
    public void ClientAndServerDto_ExposeTheSamePublicPropertyShape(string typeName)
    {
        var clientType = ResolveType("BBT.Workflow.Tasks", typeName);
        var serverType = ResolveType("BBT.Workflow.Execution", typeName);

        var clientProperties = GetWireProperties(clientType);
        var serverProperties = GetWireProperties(serverType);

        var clientOnly = clientProperties.Keys.Except(serverProperties.Keys).ToList();
        var serverOnly = serverProperties.Keys.Except(clientProperties.Keys).ToList();

        if (clientOnly.Count > 0 || serverOnly.Count > 0)
        {
            Assert.Fail(BuildMissingPropertyMessage(typeName, clientType, serverType, clientOnly, serverOnly));
        }

        foreach (var propertyName in clientProperties.Keys)
        {
            var clientShape = Shape(clientProperties[propertyName].PropertyType);
            var serverShape = Shape(serverProperties[propertyName].PropertyType);

            clientShape.ShouldBe(
                serverShape,
                $"Property '{propertyName}' on '{clientType.FullName}' has wire-shape '{clientShape}' " +
                $"but its counterpart on '{serverType.FullName}' has wire-shape '{serverShape}'. " +
                $"These two properties carry the same task-invocation wire contract and must stay " +
                $"structurally identical (a property added/changed on one side and not the other " +
                $"silently drops on the wire) -- fix whichever side is now out of sync.");
        }
    }

    private static string BuildMissingPropertyMessage(
        string typeName, Type clientType, Type serverType, List<string> clientOnly, List<string> serverOnly)
    {
        var lines = new List<string>
        {
            $"'{clientType.FullName}' and '{serverType.FullName}' (the paired '{typeName}' wire-contract " +
            "DTOs) have diverged in their public property sets. A property present on only one side " +
            "silently drops on the wire when the other side serializes/deserializes it.",
        };

        foreach (var name in clientOnly)
        {
            lines.Add($"  - '{name}' exists on '{clientType.FullName}' but not on '{serverType.FullName}'.");
        }

        foreach (var name in serverOnly)
        {
            lines.Add($"  - '{name}' exists on '{serverType.FullName}' but not on '{clientType.FullName}'.");
        }

        lines.Add("Add the missing property to whichever side is behind.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Resolves one of the paired DTO types by its simple name inside the given namespace, in
    /// the assembly that declares <see cref="BBT.Workflow.Tasks.TaskEnvelope"/> (client side) or
    /// <see cref="BBT.Workflow.Execution.TaskEnvelope"/> (server side) -- both land in the
    /// assembly closure of BBT.Workflow.Application.Tests via BBT.Workflow.Application.
    /// </summary>
    private static Type ResolveType(string ns, string typeName)
    {
        var probeAssembly = ns == "BBT.Workflow.Tasks"
            ? typeof(BBT.Workflow.Tasks.TaskEnvelope).Assembly
            : typeof(BBT.Workflow.Execution.TaskEnvelope).Assembly;

        var type = probeAssembly.GetType($"{ns}.{typeName}", throwOnError: false);
        type.ShouldNotBeNull($"Expected type '{ns}.{typeName}' to exist in assembly '{probeAssembly.GetName().Name}'.");
        return type!;
    }

    /// <summary>
    /// Public instance properties with a getter -- the ones System.Text.Json actually puts on
    /// the wire. Static factory methods (Success/Failure) and instance methods (ToContext) are
    /// deliberately excluded; they're not part of the serialized shape.
    /// </summary>
    private static Dictionary<string, PropertyInfo> GetWireProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true })
            .ToDictionary(p => p.Name, p => p);

    /// <summary>
    /// A structural "wire shape" signature for a property type, normalized so that the two DTO
    /// families' OWN paired types (e.g. Tasks.TaskEnvelope vs Execution.TaskEnvelope, referenced
    /// from TaskInvokeRequest.Envelope) compare equal by simple name across their different
    /// namespaces -- they are, by design, not the same CLR type, only the same wire shape.
    /// Everything else (primitives, Guid, JsonElement, generic collections) is compared by full
    /// structural identity, recursing into generic type arguments with the same normalization.
    /// </summary>
    private static string Shape(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (PairedTypeNames.Contains(underlying.Name))
        {
            return underlying.Name;
        }

        if (underlying.IsGenericType)
        {
            var genericDefinitionName = underlying.GetGenericTypeDefinition().Name;
            var argShapes = underlying.GetGenericArguments().Select(Shape);
            return $"{genericDefinitionName}<{string.Join(",", argShapes)}>";
        }

        return underlying.FullName ?? underlying.Name;
    }
}
