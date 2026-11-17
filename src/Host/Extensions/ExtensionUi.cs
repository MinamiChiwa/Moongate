using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 运行中扩展提供的界面：页面、浮窗元件、绑定状态与活动栏状态，全部只在内存里，扩展会话结束即撤下。
/// 界面描述（schema 版本 1）是节点树：每个节点是带 <c>type</c> 的对象，可带 <c>id</c> 与 <c>children</c>；
/// 宿主只核对结构与上限（序列化 128 KiB、深度 24、节点 400），节点类型由前端解释，未知类型降级显示。
/// 每个扩展最多 8 个页面、8 个浮窗元件；状态最多 128 个键、合计 64 KiB。
/// </summary>
public sealed class ExtensionUiRegistry
{
    public const int SchemaVersion = 1, MaximumSurfaces = 8, MaximumDescriptionBytes = 128 * 1024, MaximumDepth = 24, MaximumNodes = 400;
    public const int MaximumStateKeys = 128, MaximumStateBytes = 64 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<(string Extension, string Id), ExtensionSurface> surfaces = [];
    private readonly Dictionary<string, Dictionary<string, JsonElement>> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionActivity> activities = new(StringComparer.Ordinal);
    private ulong revision;

    public ulong Revision { get { lock (gate) return revision; } }

    public ExtensionSurface SetSurface(string extension, string kind, JsonElement p)
    {
        var id = ExtensionCapabilities.Text(p, "id", 32)!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z][a-z0-9-]{0,31}$"))
            throw new ExtensionCallException("request.invalid", "界面 id 须为小写字母开头的 1–32 个小写字母、数字或连字符");
        var titles = Titles(p);
        if (!p.TryGetProperty("root", out var root)) throw new ExtensionCallException("request.invalid", "缺少 root");
        var schema = p.TryGetProperty("schemaVersion", out var version) && version.TryGetInt32(out var value) ? value : SchemaVersion;
        if (schema != SchemaVersion) throw new ExtensionCallException("extension.schemaUnsupported", "界面描述版本 " + schema + " 不受支持，当前为 " + SchemaVersion);
        Validate(root);
        string? size = null;
        if (kind == "overlay")
        {
            size = ExtensionCapabilities.Text(p, "size", 8, optional: true) ?? "small";
            if (size is not ("small" or "medium" or "large")) throw new ExtensionCallException("request.invalid", "size 须为 small、medium 或 large");
        }
        lock (gate)
        {
            if (!surfaces.ContainsKey((extension, id)) && surfaces.Keys.Count(x => x.Extension == extension && surfaces[x].Kind == kind) >= MaximumSurfaces)
                throw new ExtensionCallException("extension.uiLimit", "每个扩展最多 " + MaximumSurfaces + " 个" + (kind == "page" ? "页面" : "浮窗元件"));
            if (surfaces.TryGetValue((extension, id), out var existing) && existing.Kind != kind)
                throw new ExtensionCallException("request.invalid", "同一 id 已用于另一类界面");
            var surface = new ExtensionSurface(extension, id, kind, titles, schema, root.Clone(), size, new(++revision));
            surfaces[(extension, id)] = surface;
            return surface;
        }
    }

    public bool RemoveSurface(string extension, string kind, string id)
    {
        lock (gate)
        {
            if (!surfaces.TryGetValue((extension, id), out var surface) || surface.Kind != kind) return false;
            surfaces.Remove((extension, id));
            revision++;
            return true;
        }
    }

    public void SetState(string extension, JsonElement p)
    {
        if (!p.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
            throw new ExtensionCallException("request.invalid", "values 必须是对象");
        lock (gate)
        {
            var current = new Dictionary<string, JsonElement>(states.GetValueOrDefault(extension) ?? [], StringComparer.Ordinal);
            foreach (var property in values.EnumerateObject())
            {
                if (property.Name.Length is 0 or > 64) throw new ExtensionCallException("request.invalid", "状态键须为 1–64 字");
                if (property.Value.ValueKind == JsonValueKind.Null) current.Remove(property.Name);
                else current[property.Name] = property.Value.Clone();
            }
            if (current.Count > MaximumStateKeys || JsonSerializer.SerializeToUtf8Bytes(current).Length > MaximumStateBytes)
                throw new ExtensionCallException("extension.uiLimit", "界面状态最多 " + MaximumStateKeys + " 个键、合计 64 KiB");
            states[extension] = current;
            revision++;
        }
    }

    public void SetActivity(string extension, JsonElement p)
    {
        // 活动栏状态行约 16 字：超过时拒绝，由扩展自行缩短，不在宿主截断改义。
        var text = ExtensionCapabilities.Text(p, "text", 16, optional: true);
        var tone = ExtensionCapabilities.Text(p, "tone", 16, optional: true) ?? "neutral";
        if (tone is not ("neutral" or "active" or "warning" or "error")) throw new ExtensionCallException("request.invalid", "tone 须为 neutral、active、warning 或 error");
        lock (gate)
        {
            if (string.IsNullOrEmpty(text)) activities.Remove(extension);
            else activities[extension] = new(extension, text, tone, new(revision + 1));
            revision++;
        }
    }

    /// <summary>撤下一个扩展的页面、浮窗元件、状态或活动栏状态；<paramref name="kinds"/> 为空时全部撤下。</summary>
    public void Remove(string extension, params string[] kinds)
    {
        lock (gate)
        {
            var all = kinds.Length == 0;
            foreach (var key in surfaces.Where(x => x.Key.Extension == extension && (all || kinds.Contains(x.Value.Kind))).Select(x => x.Key).ToArray())
                surfaces.Remove(key);
            if (all || kinds.Contains("state")) states.Remove(extension);
            if (all || kinds.Contains("activity")) activities.Remove(extension);
            revision++;
        }
    }

    public ExtensionSurface? Surface(string extension, string id)
    {
        lock (gate) return surfaces.GetValueOrDefault((extension, id));
    }

    public (IReadOnlyList<ExtensionSurface> Surfaces, IReadOnlyList<ExtensionStateValues> States, IReadOnlyList<ExtensionActivity> Activities, ulong Revision) Snapshot()
    {
        lock (gate)
            return (surfaces.Values.OrderBy(x => x.ExtensionId, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(),
                states.Select(x => new ExtensionStateValues(x.Key, x.Value, new(revision))).OrderBy(x => x.ExtensionId, StringComparer.Ordinal).ToArray(),
                activities.Values.OrderBy(x => x.ExtensionId, StringComparer.Ordinal).ToArray(), revision);
    }

    /// <summary>设置项或授权变化时由运行时调用，使前端重读设置表单与命令。</summary>
    public void Touch()
    {
        lock (gate) revision++;
    }

    private static IReadOnlyDictionary<string, string> Titles(JsonElement p)
    {
        if (!p.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Object)
            throw new ExtensionCallException("request.invalid", "titles 必须是以 zh-Hans／zh-Hant 为键的对象");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in titles.EnumerateObject())
            if (property.Name is "zh-Hans" or "zh-Hant" && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 and <= 32 } text)
                result[property.Name] = text;
            else throw new ExtensionCallException("request.invalid", "titles 只接受 zh-Hans、zh-Hant，各 1–32 字");
        return result.Count > 0 ? result : throw new ExtensionCallException("request.invalid", "titles 至少一种语言");
    }

    private static void Validate(JsonElement root)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(root).Length > MaximumDescriptionBytes)
            throw new ExtensionCallException("extension.uiLimit", "界面描述超过 128 KiB");
        var nodes = 0;
        void Visit(JsonElement node, int depth)
        {
            if (depth > MaximumDepth) throw new ExtensionCallException("extension.uiLimit", "界面描述超过 " + MaximumDepth + " 层");
            if (++nodes > MaximumNodes) throw new ExtensionCallException("extension.uiLimit", "界面描述超过 " + MaximumNodes + " 个节点");
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() is not { Length: > 0 and <= 32 })
                throw new ExtensionCallException("request.invalid", "每个界面节点必须是带 type 的对象");
            if (node.TryGetProperty("id", out var id) && (id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 64 }))
                throw new ExtensionCallException("request.invalid", "节点 id 须为 1–64 字");
            if (node.TryGetProperty("children", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array) throw new ExtensionCallException("request.invalid", "children 必须是数组");
                foreach (var child in children.EnumerateArray()) Visit(child, depth + 1);
            }
        }
        Visit(root, 1);
    }
}
