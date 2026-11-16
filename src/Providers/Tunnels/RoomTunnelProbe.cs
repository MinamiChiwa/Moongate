using System.Net.WebSockets;
using System.Text.Json;

namespace MabiTool.Providers.Tunnels;

public interface IRoomTunnelProbe
{
    Task<bool> VerifyAsync(Uri endpoint, string epoch, CancellationToken ct);
}

public sealed class RoomTunnelProbe(TimeProvider? time = null) : IRoomTunnelProbe
{
    private const int MaximumMessageBytes = 16384;
    public async Task<bool> VerifyAsync(Uri endpoint, string epoch, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time ?? TimeProvider.System);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var health = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http", Path = "/health" }.Uri;
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false,
                UseProxy = endpoint.Scheme == "wss", MaxResponseHeadersLength = 16, ConnectTimeout = TimeSpan.FromSeconds(5) })
                { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(health, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumMessageBytes) return false;
            await using var body = await response.Content.ReadAsStreamAsync(attempt.Token);
            using var document = await ReadDocumentAsync(body, attempt.Token);
            if (!Identity(document.RootElement, epoch)) return false;
            using var socket = new ClientWebSocket();
            socket.Options.Proxy = endpoint.Scheme == "wss" ? HttpClient.DefaultProxy : null;
            await socket.ConnectAsync(endpoint, attempt.Token);
            using var hello = await ReceiveAsync(socket, attempt.Token);
            if (Type(hello.RootElement) != "hello" || !Identity(hello.RootElement, epoch)) return false;
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", product = "MabiSync", protocolVersion = 1 }).AsMemory(),
                WebSocketMessageType.Text, true, attempt.Token);
            using var accepted = await ReceiveAsync(socket, attempt.Token);
            return Type(accepted.RootElement) == "hello-accepted" && Identity(accepted.RootElement, epoch);
        }
        catch (Exception error) when (error is HttpRequestException or WebSocketException or JsonException or IOException or
            InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        { ct.ThrowIfCancellationRequested(); return false; }
    }

    private static string? Type(JsonElement root) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
    private static bool Identity(JsonElement root, string epoch) =>
        root.ValueKind == JsonValueKind.Object &&
        root.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == root.EnumerateObject().Count() &&
        root.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.String && product.GetString() == "MabiSync" &&
        root.TryGetProperty("protocolVersion", out var version) && version.TryGetInt32(out var number) && number == 1 &&
        root.TryGetProperty("serverEpoch", out var identity) && identity.ValueKind == JsonValueKind.String &&
        Guid.TryParse(identity.GetString(), out var actual) && Guid.TryParse(epoch, out var expected) && actual == expected;
    private static async Task<JsonDocument> ReadDocumentAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[MaximumMessageBytes + 1]; var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) return JsonDocument.Parse(bytes.AsMemory(0, count), new() { MaxDepth = 16 });
            count += read;
        }
        throw new IOException("房间健康响应超过预算");
    }
    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[4096]; using var body = new MemoryStream();
        ValueWebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(bytes.AsMemory(), ct);
            if (frame.MessageType != WebSocketMessageType.Text || body.Length + frame.Count > MaximumMessageBytes)
                throw new IOException("房间握手消息无效或超过预算");
            body.Write(bytes, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return JsonDocument.Parse(body.ToArray(), new() { MaxDepth = 16 });
    }
}
