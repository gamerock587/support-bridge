using System.Net.WebSockets;
using System.Text.Json;
namespace JedarBridge;
public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task Send(WebSocket socket, object value, CancellationToken ct = default)
        => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text, true, ct);
    public static async Task<JsonElement?> Receive(WebSocket socket, CancellationToken ct = default)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16384];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer, ct);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text || stream.Length + part.Count > 12_000_000)
                throw new InvalidDataException("Invalid or oversized message.");
            stream.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}
public record Work(string Op, string? Command = null, string? Path = null, string? ContentBase64 = null,
    string? Cwd = null, int TimeoutSeconds = 120, bool Overwrite = false);
