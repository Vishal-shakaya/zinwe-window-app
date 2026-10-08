using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Ipc;

/// <summary>
/// Service ↔ tray protocol: one UTF-8 JSON line request, one JSON line response, then close.
/// Kept deliberately tiny; add request types here (e.g. "consent", "alerts") as modules land.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "Zinwe.Agent";

    public sealed record Request(string Type);

    public static class RequestTypes
    {
        public const string Status = "status";
    }

    internal static string Serialize(Request request) => JsonSerializer.Serialize(request, PipeJsonContext.Default.Request);

    internal static Request? DeserializeRequest(string line) => JsonSerializer.Deserialize(line, PipeJsonContext.Default.Request);

    public static string Serialize(AgentStatus status) => JsonSerializer.Serialize(status, PipeJsonContext.Default.AgentStatus);

    public static AgentStatus? DeserializeStatus(string line) => JsonSerializer.Deserialize(line, PipeJsonContext.Default.AgentStatus);

    /// <summary>Server side: read one request line. Returns null on a malformed or empty request.</summary>
    public static async Task<Request?> ReadRequestAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line) || line.Length > 4096)
        {
            return null;
        }
        try
        {
            return DeserializeRequest(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Client used by the tray app. Never throws for "service not running" -- returns null instead.</summary>
public static class AgentPipeClient
{
    public static async Task<AgentStatus?> GetStatusAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            await using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(PipeProtocol.Serialize(new PipeProtocol.Request(PipeProtocol.RequestTypes.Status)).AsMemory(), cts.Token);

            var line = await reader.ReadLineAsync(cts.Token);
            return line is null ? null : PipeProtocol.DeserializeStatus(line);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PipeProtocol.Request))]
[JsonSerializable(typeof(AgentStatus))]
internal sealed partial class PipeJsonContext : JsonSerializerContext;
