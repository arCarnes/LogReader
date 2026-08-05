namespace LogReader.Core.Models;

using System.Collections.Immutable;
using System.Text.Json;

public static class LiveLogIpcProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameBytes = 1024 * 1024;
    public const int MaximumClients = 3;

    public const string HandshakeFrame = "handshake";
    public const string HandshakeResultFrame = "handshakeResult";
    public const string RequestFrame = "request";
    public const string CancelFrame = "cancel";
    public const string ResponseFrame = "response";

    public const string ListLogTreeOperation = "listLogTree";
    public const string SearchLogsOperation = "searchLogs";
    public const string ReadLogLinesOperation = "readLogLines";
    public const string ReadLogTailOperation = "readLogTail";
    public const string ServerStatusOperation = "serverStatus";

    public static ImmutableArray<string> Capabilities { get; } =
    [
        ListLogTreeOperation,
        SearchLogsOperation,
        ReadLogLinesOperation,
        ReadLogTailOperation,
        ServerStatusOperation,
        "cancellation"
    ];
}

public sealed class LiveLogIpcFrame
{
    public string Type { get; init; } = string.Empty;

    public int ProtocolVersion { get; init; }

    public string RequestId { get; init; } = string.Empty;

    public string? StorageIdentity { get; init; }

    public string? Operation { get; init; }

    public JsonElement? Payload { get; init; }

    public bool? Success { get; init; }

    public LiveLogIpcError? Error { get; init; }

    public ImmutableArray<string> Capabilities { get; init; } = [];
}

public sealed record LiveLogIpcError(string Code, string Message, bool IsRetryable);

public sealed record LiveLogPipeIdentity(string PipeName, string StorageIdentity);
