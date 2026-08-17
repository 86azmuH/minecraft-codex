using System.Text.Json;

namespace MinecraftCodex.Companion.Protocol;

public sealed record ProtocolMessage(
    int Version,
    string Type,
    string? RequestId,
    string? TaskId,
    long? Sequence,
    object? Payload)
{
    public static ProtocolMessage Event(string type, string taskId, long sequence, object? payload = null) =>
        new(1, type, null, taskId, sequence, payload);

    public static ProtocolMessage Response(string type, string? requestId, object? payload = null) =>
        new(1, type, requestId, null, null, payload);
}

public sealed record ClientMessage(int Version, string Type, string? RequestId, JsonElement Payload);
public sealed record StartTaskPayload(string Prompt, string? WorkingDirectory);
public sealed record TaskIdPayload(string TaskId);
