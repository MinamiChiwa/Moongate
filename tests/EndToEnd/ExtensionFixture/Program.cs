using System.Text;
using System.Text.Json.Nodes;

// 扩展运行时测试用的扩展进程：第一个参数选择行为，用来验证宿主对握手、越权、超限、挂死、崩溃与迟到消息的处理。
// 只用于测试，不随发行包分发。
var mode = args.Length > 0 ? args[0] : "good";
var output = Console.OpenStandardOutput();
var writeGate = new SemaphoreSlim(1, 1);
async Task Send(JsonObject message)
{
    var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
    await writeGate.WaitAsync();
    try { await output.WriteAsync(bytes); await output.FlushAsync(); }
    finally { writeGate.Release(); }
}
Task Log(string text) => Send(new() { ["jsonrpc"] = "2.0", ["method"] = "log", ["params"] = new JsonObject { ["level"] = "info", ["message"] = text } });
var id = Environment.GetEnvironmentVariable("MOONGATE_EXTENSION_ID") ?? "test.fixture";
var version = Environment.GetEnvironmentVariable("MOONGATE_EXTENSION_VERSION") ?? "1.0.0";

if (mode == "huge")
{
    await output.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 16) + "\n"));
    await Task.Delay(Timeout.Infinite);
}
await Send(new()
{
    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "hello",
    ["params"] = new JsonObject { ["protocolVersion"] = mode == "bad-version" ? 2 : 1, ["extensionId"] = id, ["version"] = version }
});

var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var next = 100;
var hung = false;
while (await reader.ReadLineAsync() is { } line)
{
    var message = JsonNode.Parse(line)!.AsObject();
    var method = message["method"]?.GetValue<string>();
    if (method is null)
    {
        // 宿主对本进程调用的响应：把结果或原因码写进日志，测试据此断言。
        var reason = message["error"]?["data"]?["reason"]?.GetValue<string>();
        var answer = message["id"]!.GetValue<int>();
        if (answer == 1) await Started();
        else await Log("reply " + answer + " " + (reason ?? "ok " + message["result"]?.ToJsonString()));
        if (mode == "pick" && answer == 101) await Call("files.pick", new JsonObject { ["title"] = "再选一次" });
        continue;
    }
    if (method == "ping")
    {
        if (hung) continue;
        if (mode == "late") { _ = Task.Run(async () => { await Task.Delay(800); await Send(new() { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = new JsonObject() }); }); continue; }
        await Send(new() { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = new JsonObject() });
        continue;
    }
    if (method == "shutdown")
    {
        if (mode == "ignore-shutdown") continue;
        await Send(new() { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = new JsonObject() });
        return 0;
    }
    if (message["id"] is null) await Log("notify " + method + " " + message["params"]?.ToJsonString());
}
return 0;

async Task Started()
{
    await Log("started " + mode);
    switch (mode)
    {
        case "rogue-call":
            await Call("combat.snapshot", new JsonObject());
            await Call("network.fetch", new JsonObject { ["url"] = "https://example.com/" });
            await Call("no.such.method", new JsonObject());
            break;
        case "subscribe":
            await Call("events.subscribe", new JsonObject { ["topics"] = new JsonArray("combat.measurement.changed") });
            break;
        case "storage":
            await Call("storage.set", new JsonObject { ["key"] = "k", ["value"] = new JsonObject { ["n"] = 1 } });
            await Call("storage.get", new JsonObject { ["key"] = "k" });
            break;
        case "page":
            await Call("ui.page.set", new JsonObject { ["id"] = "main", ["titles"] = new JsonObject { ["zh-Hans"] = "测试" },
                ["root"] = new JsonObject { ["type"] = "column", ["children"] = new JsonArray(new JsonObject { ["type"] = "button", ["id"] = "go", ["text"] = "Go" }) } });
            await Call("ui.overlay.set", new JsonObject { ["id"] = "mini", ["titles"] = new JsonObject { ["zh-Hans"] = "迷你" }, ["size"] = "small",
                ["root"] = new JsonObject { ["type"] = "value", ["bind"] = "n" } });
            await Call("ui.state.set", new JsonObject { ["values"] = new JsonObject { ["n"] = 7 } });
            break;
        case "input":
            await Call("input.acquire", new JsonObject { ["mode"] = "background", ["seconds"] = 60 });
            break;
        case "network":
            await Call("network.fetch", new JsonObject { ["url"] = "http://api.example.test/ok" });
            await Call("network.fetch", new JsonObject { ["url"] = "http://other.example.test/ok" });
            await Call("network.fetch", new JsonObject { ["url"] = "http://api.example.test/big" });
            await Call("network.fetch", new JsonObject { ["url"] = "http://api.example.test/slow" });
            break;
        case "capture":
            await Call("capture.game", new JsonObject { ["maxWidth"] = 16 });
            await Call("capture.game", new JsonObject { ["maxWidth"] = 16 });
            break;
        case "pick":
            await Call("files.pick", new JsonObject { ["title"] = "选择存档" });
            break;
        case "flood":
            for (var i = 0; i < 2000; i++) await Log("flood " + i);
            break;
        case "hang":
            hung = true;
            break;
        case "crash":
            Environment.Exit(3);
            break;
    }
}

Task Call(string name, JsonObject parameters) =>
    Send(new() { ["jsonrpc"] = "2.0", ["id"] = ++next, ["method"] = name, ["params"] = parameters });
