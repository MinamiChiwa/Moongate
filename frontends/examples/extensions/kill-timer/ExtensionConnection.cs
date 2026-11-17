using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Moongate.Examples.KillTimer;

/// <summary>
/// Moongate 扩展协议版本 1 的最小客户端：标准输入输出上的行分隔 JSON-RPC 2.0。
/// 扩展先发 hello 完成握手，之后可以调用宿主方法、接收宿主通知（event、ui/event、settings/changed、grants/changed）
/// 并响应宿主请求（ping、shutdown）。日志写到标准错误或经 log 通知写入扩展日志。
/// </summary>
public sealed class ExtensionConnection
{
    private readonly Stream input = Console.OpenStandardInput();
    private readonly Stream output = Console.OpenStandardOutput();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long nextId;

    /// <summary>宿主发来的通知：方法名与参数。</summary>
    public event Func<string, JsonObject, Task>? Notification;

    /// <summary>宿主请求退出（shutdown）或标准输入关闭时完成。</summary>
    public Task Stopped => stopped.Task;

    public async Task<JsonObject> StartAsync(string extensionId, string version)
    {
        _ = Task.Run(ReadLoopAsync);
        var result = await CallAsync("hello", new JsonObject
        {
            ["protocolVersion"] = 1, ["extensionId"] = extensionId, ["version"] = version
        });
        return result?.AsObject() ?? new JsonObject();
    }

    /// <summary>调用宿主方法；宿主返回错误时抛出 <see cref="HostCallException"/>，其中 Reason 是稳定原因码。</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null)
    {
        var id = Interlocked.Increment(ref nextId);
        var source = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = source;
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() });
        return await source.Task;
    }

    public Task LogAsync(string level, string message) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "log", ["params"] = new JsonObject { ["level"] = level, ["message"] = message } });

    private async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await writeGate.WaitAsync();
        try
        {
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        finally { writeGate.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            var message = JsonNode.Parse(line)!.AsObject();
            if (message["method"] is { } method)
            {
                var name = method.GetValue<string>();
                var parameters = message["params"]?.AsObject() ?? new JsonObject();
                if (message["id"] is { } id)
                {
                    // 宿主请求：ping 立即回应，shutdown 回应后结束。
                    await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject() });
                    if (name == "shutdown") { stopped.TrySetResult(); return; }
                }
                else if (Notification is { } handler) _ = Task.Run(() => handler(name, parameters));
                continue;
            }
            var key = message["id"]!.GetValue<long>();
            if (!pending.TryRemove(key, out var source)) continue;
            if (message["error"] is { } error)
                source.TrySetException(new HostCallException(error["data"]?["reason"]?.GetValue<string>() ?? "unknown", error["message"]?.GetValue<string>() ?? string.Empty));
            else source.TrySetResult(message["result"]);
        }
        stopped.TrySetResult();
    }
}

public sealed class HostCallException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}
