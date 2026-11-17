using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MabiTool.Host.Extensions;

public sealed class ExtensionProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 一条协议消息（JSON-RPC 2.0 形式）。请求有 <see cref="Method"/> 与 <see cref="Id"/>，通知只有 <see cref="Method"/>，
/// 响应有 <see cref="Id"/> 及 <see cref="Result"/> 或 <see cref="Error"/> 之一。
/// </summary>
public sealed record ExtensionMessage(string? Id, string? Method, JsonElement Params, JsonElement? Result, JsonElement? Error)
{
    public bool IsRequest => Method is not null && Id is not null;
    public bool IsNotification => Method is not null && Id is null;
    public bool IsResponse => Method is null;
}

/// <summary>
/// 扩展协议版本 1：子进程的标准输入输出承载 UTF-8 行分隔 JSON，每行一条消息，单行不超过 1 MiB。
/// 帧与握手沿用 Worker 协议的约定：先握手核对版本与身份，重复字段与未知字段拒绝，控制与业务消息共用一条通道，
/// 日志另走标准错误。标准输入输出便于任何语言实现；命名管道与长度前缀留给宿主自有的 Worker。
/// </summary>
public static class ExtensionProtocol
{
    public const int Version = 1;
    public const int MaximumLineBytes = 1024 * 1024;
    private static readonly string[] Fields = ["jsonrpc", "id", "method", "params", "result", "error"];

    /// <summary>解析一行；不合协议时抛出 <c>extension.protocolViolation</c>。</summary>
    public static ExtensionMessage Parse(ReadOnlySpan<byte> line)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw Violation("消息不是有效 JSON"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Violation("消息必须是 JSON 对象");
            Unique(root);
            var names = root.EnumerateObject().Select(x => x.Name).ToArray();
            if (names.Any(x => !Fields.Contains(x, StringComparer.Ordinal))) throw Violation("消息含未知字段");
            if (!root.TryGetProperty("jsonrpc", out var marker) || marker.ValueKind != JsonValueKind.String || marker.GetString() != "2.0")
                throw Violation("jsonrpc 必须为 \"2.0\"");
            string? id = null;
            if (root.TryGetProperty("id", out var idElement))
                id = idElement.ValueKind switch
                {
                    JsonValueKind.Number when idElement.TryGetInt64(out var number) && number is >= 0 and <= 9007199254740991 => "n:" + number,
                    JsonValueKind.String when idElement.GetString() is { Length: > 0 and <= 64 } text => "s:" + text,
                    _ => throw Violation("id 必须是非负整数或不超过 64 字的文字")
                };
            if (root.TryGetProperty("method", out var methodElement))
            {
                if (methodElement.ValueKind != JsonValueKind.String || methodElement.GetString() is not { Length: > 0 and <= 64 } method ||
                    root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _))
                    throw Violation("method 无效，或请求同时带有 result／error");
                var parameters = root.TryGetProperty("params", out var p) ? p : default;
                if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) throw Violation("params 必须是对象");
                return new(id, method, parameters.ValueKind == JsonValueKind.Object ? parameters.Clone() : EmptyObject(), null, null);
            }
            if (id is null || root.TryGetProperty("params", out _)) throw Violation("响应必须带 id，且不带 params");
            var hasResult = root.TryGetProperty("result", out var result);
            var hasError = root.TryGetProperty("error", out var error);
            if (hasResult == hasError) throw Violation("响应必须且只能带 result 或 error 之一");
            if (hasError && error.ValueKind != JsonValueKind.Object) throw Violation("error 必须是对象");
            return new(id, null, EmptyObject(), hasResult ? result.Clone() : null, hasError ? error.Clone() : null);
        }
    }

    public static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>把内部 id（带类型前缀）还原为线路上的 JSON 值。</summary>
    public static JsonNode? WireId(string id) => id.StartsWith("n:", StringComparison.Ordinal)
        ? JsonValue.Create(long.Parse(id[2..], System.Globalization.CultureInfo.InvariantCulture))
        : JsonValue.Create(id[2..]);

    public static byte[] Request(string id, string method, object? parameters) => Line(new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = WireId(id), ["method"] = method, ["params"] = Node(parameters)
    });

    public static byte[] Notification(string method, object? parameters) => Line(new JsonObject
    {
        ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = Node(parameters)
    });

    public static byte[] Result(string id, object? result) => Line(new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = WireId(id), ["result"] = Node(result) ?? new JsonObject()
    });

    /// <summary>错误响应：<c>error.data.reason</c> 是稳定原因码，<c>message</c> 是给人看的说明。</summary>
    public static byte[] Failure(string id, string reason, string message) => Line(new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = WireId(id),
        ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message, ["data"] = new JsonObject { ["reason"] = reason } }
    });

    private static JsonNode? Node(object? value) => value switch
    {
        null => new JsonObject(),
        JsonNode node => node,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, MabiTool.Contracts.ContractJson.Options)
    };

    private static byte[] Line(JsonObject value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString() + "\n");
        return bytes.Length <= MaximumLineBytes ? bytes : throw new ExtensionProtocolException("extension.messageTooLarge", "消息超过 1 MiB");
    }

    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Violation("消息含重复字段：" + property.Name);
                Unique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Unique(item);
    }

    private static ExtensionProtocolException Violation(string message) => new("extension.protocolViolation", message);
}

/// <summary>按行读取字节流；单行超过上限时抛出 <c>extension.messageTooLarge</c>，不为超长行无限分配内存。</summary>
public sealed class ExtensionLineReader(Stream stream, int maximumLineBytes = ExtensionProtocol.MaximumLineBytes)
{
    private readonly byte[] buffer = new byte[64 * 1024];
    private int start, end;

    /// <summary>读取下一行（不含换行与行尾的回车）；流结束时返回空。</summary>
    public async Task<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            if (newline >= 0)
            {
                line.Write(buffer, start, newline - start);
                start = newline + 1;
                var bytes = line.ToArray();
                if (bytes.Length > maximumLineBytes) throw TooLarge();
                return bytes is [.., (byte)'\r'] ? bytes[..^1] : bytes;
            }
            line.Write(buffer, start, end - start);
            start = end = 0;
            if (line.Length > maximumLineBytes) throw TooLarge();
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return line.Length == 0 ? null : throw new ExtensionProtocolException("extension.protocolViolation", "最后一条消息没有换行");
            end = read;
        }
    }

    private static ExtensionProtocolException TooLarge() => new("extension.messageTooLarge", "单条消息超过 1 MiB");
}
