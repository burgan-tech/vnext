using System.Text.Json;
using Google.Protobuf;

namespace BBT.Workflow.Execution.Grpc;

/// <summary>
/// Serializes the task-invocation DTOs into the gRPC payload bytes and back.
/// <para>
/// One place on purpose: the gRPC transport must exchange exactly the JSON the HTTP
/// endpoint exchanges (ASP.NET Core web defaults — camelCase, case-insensitive read),
/// so the serializer options live here once instead of at each call site where they
/// could drift apart.
/// </para>
/// </summary>
public static class TaskInvokePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes <paramref name="value"/> to the UTF-8 JSON payload bytes.</summary>
    public static ByteString Serialize<T>(T value)
        => ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value, Options));

    /// <summary>Deserializes the payload bytes produced by <see cref="Serialize{T}"/>.</summary>
    public static T Deserialize<T>(ByteString payload)
        => JsonSerializer.Deserialize<T>(payload.Span, Options)!;
}
