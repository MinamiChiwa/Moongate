using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Host.Combat;
using MabiTool.Host.Game;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Modules.Inventory.Adapters;
using MabiTool.Modules.MusicLibrary.Persistence;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 能力代理：扩展的每次调用先核对方法对应的权限是否声明且已授予，再转给宿主既有的模块接口。
/// 代理不另开读写路径：数据读取复用各模块的查询与名称投影，事件复用统一事件中心的订阅与可见性规则，
/// 输入复用输入仲裁、绑定核对、租约与紧急停止。宿主服务按需从容器取得，嵌入宿主缺少某项服务时该能力返回 capability.unavailable。
/// </summary>
public sealed partial class ExtensionCapabilities(IServiceProvider services, ExtensionInstaller installer, TimeProvider time)
{
    /// <summary>单个结果的上限，留出协议外层的余量。</summary>
    public const int MaximumResultBytes = ExtensionProtocol.MaximumLineBytes - 4096;
    private TimeProvider Clock => time;

    public static bool Allowed(string method, IReadOnlySet<string> grants) =>
        ExtensionPermissions.Unrestricted.Contains(method) ||
        ExtensionPermissions.All.Any(x => grants.Contains(x.Id) && x.Methods.Contains(method, StringComparer.Ordinal));

    public async Task<object?> InvokeAsync(ExtensionSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        if (!ExtensionPermissions.Unrestricted.Contains(method) && ExtensionPermissions.ForMethod(method) is null && !Handled(method))
            throw new ExtensionCallException("extension.methodUnknown", "宿主不提供方法 " + method);
        if (!Allowed(method, session.Grants))
            throw new ExtensionCallException("extension.permissionDenied", "调用 " + method + " 需要的权限未声明或未授予");
        var result = await DispatchAsync(session, method, parameters, ct);
        ct.ThrowIfCancellationRequested();
        if (result is not null && JsonSerializer.SerializeToUtf8Bytes(result, result.GetType(), ContractJson.Options).Length > MaximumResultBytes)
            throw new ExtensionCallException("extension.resultTooLarge", "结果超过 1 MiB，请缩小查询范围");
        return result;
    }

    private static bool Handled(string method) => method is "settings.get";

    private async Task<object?> DispatchAsync(ExtensionSession session, string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "log":
                session.Log.Write(Text(p, "level", 16, optional: true) is "debug" or "warning" or "error" ? Text(p, "level", 16)! : "info", "extension", Text(p, "message", 4000) ?? string.Empty);
                return new JsonObject();
            case "events.subscribe": return await SubscribeAsync(session, Topics(p), add: true, ct);
            case "events.unsubscribe": return await SubscribeAsync(session, Topics(p), add: false, ct);
            case "combat.snapshot":
            {
                var store = Service<CombatHistoryStore>();
                ICombatNameResolver names = services.GetService<CombatNameCatalog>() is { } catalog ? catalog : NoNames.Instance;
                var workspace = store.Workspace(ExtensionPrincipal.Owner, owner: true);
                return new JsonObject
                {
                    ["revision"] = workspace?.Revision.ToString(),
                    ["workspace"] = workspace is null ? null : JsonSerializer.SerializeToNode(CombatNameProjection.Project(workspace, names), ContractJson.Options)
                };
            }
            case "inventory.accounts": return Service<InventoryHistoryStore>().Accounts(Read<InventoryAccountQuery>(p, "query"));
            case "inventory.items": return Service<InventoryHistoryStore>().Items(Read<InventoryItemQuery>(p, "query"));
            case "game.binding": return Service<GameBindingService>().Snapshot();
            case "game.observation": return Service<GameObservationService>().Snapshot();
            case "rooms.state": return Service<RoomSessionManager>().Get(ExtensionPrincipal.Owner);
            case "music.query":
            {
                await using var demand = await Service<ModuleRuntime>().AcquireAsync("music-library", "extension-music-query", ct);
                return Service<MusicLibraryStore>().Query(Read<MusicQuery>(p, "query") ?? throw new ExtensionCallException("request.invalid", "缺少 query"));
            }
            case "settings.get": return SettingsNode(session);
            case "storage.get": return new JsonObject { ["value"] = Storage(session).Get(Text(p, "key", 128)!) is { } stored ? JsonNode.Parse(stored.GetRawText()) : null };
            case "storage.set":
                if (!p.TryGetProperty("value", out var value)) throw new ExtensionCallException("request.invalid", "缺少 value");
                Storage(session).Set(Text(p, "key", 128)!, value);
                return new JsonObject();
            case "storage.delete": return new JsonObject { ["deleted"] = Storage(session).Delete(Text(p, "key", 128)!) };
            case "storage.list": return new JsonObject { ["keys"] = new JsonArray(Storage(session).List().Select(x => (JsonNode)x!).ToArray()) };
            case "storage.file.read":
                return new JsonObject { ["content"] = Storage(session).ReadFile(Text(p, "name", 64)!) is { } bytes ? Convert.ToBase64String(bytes) : null };
            case "storage.file.write":
            {
                byte[] content;
                try { content = Convert.FromBase64String(Text(p, "content", ExtensionProtocol.MaximumLineBytes) ?? string.Empty); }
                catch (FormatException) { throw new ExtensionCallException("request.invalid", "content 必须是 Base64"); }
                Storage(session).WriteFile(Text(p, "name", 64)!, content);
                return new JsonObject();
            }
            case "storage.file.delete": return new JsonObject { ["deleted"] = Storage(session).DeleteFile(Text(p, "name", 64)!) };
            default:
                return await DispatchExtendedAsync(session, method, p, ct);
        }
    }

    /// <summary>界面与高风险能力的分派；未登记的方法返回 methodUnknown。</summary>
    private partial Task<object?> DispatchExtendedAsync(ExtensionSession session, string method, JsonElement p, CancellationToken ct);

    /// <summary>授权变化后释放失去权限的资源：退订不再允许的事件主题、交还输入租约、撤下界面。</summary>
    public async Task GrantsChangedAsync(ExtensionSession session)
    {
        string[] keep;
        lock (session.Resources.Gate) keep = session.Resources.Topics.Where(x => ExtensionPermissions.ForTopic(x) is { } permission && session.Grants.Contains(permission.Id)).ToArray();
        await ResetSubscriptionAsync(session, keep);
        await ExtendedGrantsChangedAsync(session);
    }

    private partial Task ExtendedGrantsChangedAsync(ExtensionSession session);

    /// <summary>会话结束：停止事件转发，释放输入租约与按下的输入，撤下界面。</summary>
    public async Task ReleaseAsync(ExtensionSession session)
    {
        await ResetSubscriptionAsync(session, []);
        await ExtendedReleaseAsync(session);
    }

    private partial Task ExtendedReleaseAsync(ExtensionSession session);

    /// <summary>握手与 settings.get 返回的设置值：已保存的值，缺失时为清单默认值。</summary>
    public JsonObject SettingsNode(ExtensionSession session) => Settings(session.Id, session.Extension.Manifest);

    public JsonObject Settings(string id, ExtensionManifest manifest)
    {
        var saved = SavedSettings(id);
        var result = new JsonObject();
        foreach (var item in manifest.SettingItems)
            result[item.Key] = JsonNode.Parse((saved.TryGetValue(item.Key, out var value) && ExtensionPackageInspector.SettingAccepts(item, value) ? value : item.Default).GetRawText());
        return result;
    }

    private Dictionary<string, JsonElement> SavedSettings(string id)
    {
        var path = Path.Combine(installer.DataDirectory(id), "settings.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(path)) ?? [] : []; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }

    private async Task<object> SubscribeAsync(ExtensionSession session, IReadOnlyList<string> topics, bool add, CancellationToken ct)
    {
        foreach (var topic in topics)
            if (add && (ExtensionPermissions.ForTopic(topic) is not { } permission || !session.Grants.Contains(permission.Id)))
                throw new ExtensionCallException("extension.permissionDenied", "订阅 " + topic + " 需要的权限未声明或未授予");
        string[] next;
        lock (session.Resources.Gate)
            next = add ? session.Resources.Topics.Union(topics, StringComparer.Ordinal).ToArray() : session.Resources.Topics.Except(topics, StringComparer.Ordinal).ToArray();
        ct.ThrowIfCancellationRequested();
        await ResetSubscriptionAsync(session, next);
        return new JsonObject { ["topics"] = new JsonArray(next.Order(StringComparer.Ordinal).Select(x => (JsonNode)x!).ToArray()) };
    }

    /// <summary>以扩展主体重建一个事件订阅；主题为空时只停止转发。</summary>
    private async Task ResetSubscriptionAsync(ExtensionSession session, IReadOnlyList<string> topics)
    {
        CancellationTokenSource? previous;
        lock (session.Resources.Gate)
        {
            previous = session.Resources.EventPump;
            session.Resources.EventPump = null;
            session.Resources.Topics.Clear();
            foreach (var topic in topics) session.Resources.Topics.Add(topic);
        }
        if (previous is not null) await previous.CancelAsync();
        if (topics.Count == 0 || session.Lifetime.IsCancellationRequested) return;
        var hub = services.GetService<StateEventHub>() ?? throw new ExtensionCallException("capability.unavailable", "当前宿主没有事件中心");
        EventSubscription subscription;
        try { subscription = await hub.SubscribeAsync(ExtensionPrincipal.Session(session.Id), EventFilter.Create(topics)); }
        catch (OperationException error) { throw new ExtensionCallException(error.Code, error.Message); }
        var pump = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        lock (session.Resources.Gate) session.Resources.EventPump = pump;
        _ = Task.Run(() => PumpAsync(session, subscription, pump.Token));
    }

    private static async Task PumpAsync(ExtensionSession session, EventSubscription subscription, CancellationToken ct)
    {
        await using (subscription)
        {
            subscription.ReleaseReplay();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (subscription.NeedsResync)
                    {
                        await session.NotifyAsync("events/resync", new JsonObject { ["reason"] = "扩展读取事件过慢，队列已满；请重新读取快照后再订阅" });
                        return;
                    }
                    if (subscription.TryRead(out var item))
                    {
                        // 授权可能在排队期间被撤销：转发前再按当前授权核对一次。
                        if (ExtensionPermissions.ForTopic(item!.Type) is not { } permission || !session.Grants.Contains(permission.Id)) continue;
                        try
                        {
                            await session.NotifyAsync("event", new JsonObject
                            {
                                ["type"] = item.Type, ["resourceId"] = item.ResourceId, ["data"] = JsonNode.Parse(item.Data.GetRawText())
                            });
                        }
                        catch (ExtensionProtocolException) { session.Log.Write("warning", "host", item.Type + " 事件超过 1 MiB，未转发"); }
                        continue;
                    }
                    if (!await subscription.WaitAsync(ct)) return;
                }
            }
            catch (OperationCanceledException) { }
        }
    }

    private ExtensionStorage Storage(ExtensionSession session) => new(installer.DataDirectory(session.Id));

    private T Service<T>() where T : class => services.GetService<T>()
        ?? throw new ExtensionCallException("capability.unavailable", "当前宿主没有提供这项能力");

    private static T? Read<T>(JsonElement p, string name) where T : class
    {
        if (!p.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        try { return value.Deserialize<T>(ContractJson.Options); }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        { throw new ExtensionCallException("request.invalid", name + " 无效：" + error.Message); }
    }

    private static IReadOnlyList<string> Topics(JsonElement p)
    {
        if (!p.TryGetProperty("topics", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > 16)
            throw new ExtensionCallException("request.invalid", "topics 必须是 1–16 项的数组");
        return list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } text
            ? text : throw new ExtensionCallException("request.invalid", "topics 含无效项")).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static string? Text(JsonElement p, string name, int maximum, bool optional = false)
    {
        if (!p.TryGetProperty(name, out var value)) return optional ? null : throw new ExtensionCallException("request.invalid", "缺少 " + name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maximum
            ? text : throw new ExtensionCallException("request.invalid", name + " 必须是不超过 " + maximum + " 字的文字");
    }

    private sealed class NoNames : ICombatNameResolver
    {
        public static readonly NoNames Instance = new();
        public CombatResolvedName Resolve(string kind, uint sheetId) => new(kind, new(sheetId), null, "unavailable", "combat.nameCatalogUnavailable");
    }
}

/// <summary>
/// 扩展读取与订阅使用的主体。读取的是宿主所有者（桌面）可见的同一份数据，可见范围由权限与方法限定；
/// 事件订阅以独立的客户端标识计入订阅上限，不占用桌面前端的连接数。
/// </summary>
public static class ExtensionPrincipal
{
    public const string Owner = "owner";
    public static AuthSession Session(string id) => new("extension:" + id, "extension " + id,
        [ApiPermissions.SystemRead, ApiPermissions.GameObserve], null, null, true);
}
