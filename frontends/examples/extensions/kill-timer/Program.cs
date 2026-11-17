using System.Globalization;
using System.Text.Json.Nodes;
using Moongate.Examples.KillTimer;

// 击杀计时器：订阅战斗事件，显示当前战斗用时与最近几次击杀用时；提供一个页面、一个浮窗元件和两个设置项。
// 只用普通权限（combat.read、ui.page、ui.overlay、ui.settings、storage.local），不访问网络，不操作游戏。
var connection = new ExtensionConnection();
var gate = new object();
var kills = new List<JsonObject>();
double? currentMs = null;
var keep = 10;
var showMilliseconds = false;

string Duration(double? milliseconds) => milliseconds is not { } value ? "未读取"
    : showMilliseconds ? TimeSpan.FromMilliseconds(value).ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture)
    : TimeSpan.FromMilliseconds(value).ToString(@"m\:ss", CultureInfo.InvariantCulture);

void ApplySettings(JsonObject? values)
{
    if (values?["keep"] is { } k) keep = Math.Clamp((int)k.GetValue<double>(), 5, 50);
    if (values?["showMilliseconds"] is { } m) showMilliseconds = m.GetValue<bool>();
}

async Task PublishAsync()
{
    JsonObject values;
    lock (gate)
    {
        while (kills.Count > keep) kills.RemoveAt(kills.Count - 1);
        values = new JsonObject
        {
            ["current"] = Duration(currentMs),
            ["last"] = kills.Count == 0 ? "还没有击杀记录" : "上次击杀 " + kills[0]["duration"]!.GetValue<string>(),
            ["kills"] = new JsonArray(kills.Select(x => (JsonNode)x.DeepClone()).ToArray())
        };
    }
    await connection.CallAsync("ui.state.set", new JsonObject { ["values"] = values });
}

async Task SaveAsync()
{
    JsonArray copy;
    lock (gate) copy = new JsonArray(kills.Select(x => (JsonNode)x.DeepClone()).ToArray());
    await connection.CallAsync("storage.set", new JsonObject { ["key"] = "kills", ["value"] = copy });
}

connection.Notification += async (method, parameters) =>
{
    switch (method)
    {
        case "event" when parameters["type"]?.GetValue<string>() == "combat.summary.changed":
            lock (gate) currentMs = parameters["data"]?["durationMs"]?.GetValue<double>();
            await PublishAsync();
            break;
        case "event" when parameters["type"]?.GetValue<string>() == "combat.encounter.closed":
        {
            var summary = parameters["data"]?["summary"];
            var duration = summary?["durationMs"]?.GetValue<double>();
            var dps = summary?["encounterDps"]?.GetValue<double?>();
            lock (gate)
            {
                kills.Insert(0, new JsonObject
                {
                    ["time"] = DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ["duration"] = Duration(duration),
                    ["dps"] = dps is { } value ? value.ToString("N0", CultureInfo.InvariantCulture) : "未知"
                });
                currentMs = null;
            }
            await connection.LogAsync("info", "记录一次击杀，用时 " + Duration(duration));
            await PublishAsync();
            await SaveAsync();
            break;
        }
        case "ui/event" when parameters["node"]?.GetValue<string>() == "reset":
            lock (gate) kills.Clear();
            await PublishAsync();
            await SaveAsync();
            break;
        case "settings/changed":
            ApplySettings(parameters["values"]?.AsObject());
            await PublishAsync();
            break;
    }
};

var hello = await connection.StartAsync(
    Environment.GetEnvironmentVariable("MOONGATE_EXTENSION_ID") ?? "example.kill-timer",
    Environment.GetEnvironmentVariable("MOONGATE_EXTENSION_VERSION") ?? "1.0.0");
ApplySettings(hello["settings"]?.AsObject());
await connection.LogAsync("info", "击杀计时器已启动");

if ((await connection.CallAsync("storage.get", new JsonObject { ["key"] = "kills" }))?["value"] is JsonArray saved)
    lock (gate) kills.AddRange(saved.OfType<JsonObject>().Select(x => (JsonObject)x.DeepClone()));

var titles = new JsonObject { ["zh-Hans"] = "击杀计时", ["zh-Hant"] = "擊殺計時" };
await connection.CallAsync("ui.page.set", new JsonObject
{
    ["id"] = "kill-timer", ["titles"] = titles.DeepClone(), ["schemaVersion"] = 1,
    ["root"] = JsonNode.Parse("""
        {"type":"column","children":[
          {"type":"card","title":{"zh-Hans":"当前战斗","zh-Hant":"目前戰鬥"},"children":[
            {"type":"value","label":{"zh-Hans":"用时","zh-Hant":"用時"},"bind":"current"}]},
          {"type":"card","title":{"zh-Hans":"最近击杀","zh-Hant":"最近擊殺"},"children":[
            {"type":"table","bind":"kills","columns":[
              {"key":"time","title":{"zh-Hans":"时间","zh-Hant":"時間"}},
              {"key":"duration","title":{"zh-Hans":"用时","zh-Hant":"用時"}},
              {"key":"dps","title":"DPS"}]},
            {"type":"row","children":[{"type":"button","id":"reset","text":{"zh-Hans":"清空记录","zh-Hant":"清空紀錄"}}]}]}]}
        """)
});
await connection.CallAsync("ui.overlay.set", new JsonObject
{
    ["id"] = "kill-timer", ["titles"] = titles.DeepClone(), ["schemaVersion"] = 1, ["size"] = "small",
    ["root"] = JsonNode.Parse("""
        {"type":"column","children":[
          {"type":"value","label":{"zh-Hans":"用时","zh-Hant":"用時"},"bind":"current"},
          {"type":"text","style":"muted","bind":"last"}]}
        """)
});
await connection.CallAsync("events.subscribe", new JsonObject { ["topics"] = new JsonArray("combat.summary.changed", "combat.encounter.closed") });
await PublishAsync();
await connection.Stopped;
