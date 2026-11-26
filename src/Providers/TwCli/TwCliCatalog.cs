using System.Text.Json;

namespace MabiTool.Providers.TwCli;

/// <summary>目录里的一条指令。<see cref="RequiresConfirm"/> 兼容布尔值与字符串（当前快照里出现过 <c>"true"</c>）。</summary>
public sealed record TwCliCommand(string Name, string? Description, string? Note, bool RequiresConfirm);

/// <summary>运行中游戏返回的指令目录；<see cref="Loading"/> 为真表示客户端报告目录尚未完全就绪。</summary>
public sealed record TwCliCatalog(bool Loading, IReadOnlyDictionary<string, TwCliCommand> Commands)
{
    public int Count => Commands.Count;
    public bool Has(string command) => Commands.ContainsKey(command);
    public IReadOnlyList<string> Missing(IEnumerable<string> required) => required.Where(x => !Commands.ContainsKey(x)).ToArray();

    /// <summary>解析 <c>capabilities</c> 响应：根节点为含 <c>commands</c> 数组的对象，或直接为数组；返回空表示无法解析。</summary>
    public static TwCliCatalog? Parse(JsonElement? root)
    {
        if (root is not { } value) return null;
        var loading = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("loading", out var flag) && Flag(flag);
        JsonElement list;
        if (value.ValueKind == JsonValueKind.Array) list = value;
        else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array) list = commands;
        else return null;
        var result = new Dictionary<string, TwCliCommand>(StringComparer.Ordinal);
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("Command", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString())) continue;
            var confirm = entry.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("requiresConfirm", out var requires) && Flag(requires);
            result[name.GetString()!] = new(name.GetString()!, Text(entry, "Description"), Text(entry, "Note"), confirm);
        }
        return new(loading, result);
    }

    private static bool Flag(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
        _ => false
    };

    private static string? Text(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
