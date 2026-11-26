using System.Text.Json;

namespace MabiTool.Desktop.Pages.Inventory;

/// <summary>窗口的位置与尺寸（逻辑像素）以及是否最大化。</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized = false);

/// <summary>
/// 背包页的显示选择：所选账号、隐藏的列组（背包、角色保管箱、公共保管箱）与背包窗口的位置尺寸。
/// 页面与背包窗口共用，变化时保存到前端偏好文件。
/// </summary>
public static class InventoryPreference
{
    public const string BackpackGroup = "backpack";
    public const string StorageGroup = "storage";
    public const string SharedGroup = "shared";

    /// <summary>估价列组的标识；估价默认关闭，只在本次运行里记住（不写入偏好文件），打开后才向交易所取价。</summary>
    public const string ValuationGroup = "valuation";

    public static string? Account { get; private set; }
    public static IReadOnlyList<string> HiddenGroups { get; private set; } = [];
    public static WindowPlacement? Window { get; private set; }

    public static event EventHandler? Changed;

    /// <summary>估价开关的变化（页面与背包窗口共用；不触发偏好保存）。</summary>
    public static event EventHandler? ValuationChanged;

    /// <summary>估价开关当前是否打开；默认关闭。</summary>
    public static bool ValuationShown { get; private set; }

    public static void SetValuationShown(bool shown)
    {
        if (ValuationShown == shown) return;
        ValuationShown = shown;
        ValuationChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SetAccount(string? id)
    {
        if (Account == id) return;
        Account = id;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetGroupShown(string group, bool shown)
    {
        var next = HiddenGroups.Where(item => item != group).ToList();
        if (!shown) next.Add(group);
        if (next.Order().SequenceEqual(HiddenGroups.Order())) return;
        HiddenGroups = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetWindow(WindowPlacement placement)
    {
        if (Window == placement) return;
        Window = placement;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>读取偏好时恢复，不触发保存。</summary>
    internal static void Restore(string? account, IReadOnlyList<string>? hidden, WindowPlacement? window)
    {
        Account = account;
        HiddenGroups = hidden?.ToList() ?? [];
        Window = window;
    }
}

/// <summary>
/// 背包表“备注”列的内容，按“账号 + 道具类型键”保存在前端本机文件里。文件缺失或损坏时为空；
/// 写入先写临时文件再替换，中途失败不会写坏原文件。页面与背包窗口共用 <see cref="Shared"/>。
/// </summary>
public sealed class InventoryNoteStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string? path;
    private Dictionary<string, Dictionary<string, string>>? notes;

    /// <param name="path">保存位置；为空时只在内存里保存（测试与样本）。</param>
    public InventoryNoteStore(string? path = null) => this.path = path;

    /// <summary>应用启动时换成默认位置的文件；未设置时只在内存里。</summary>
    public static InventoryNoteStore Shared { get; set; } = new();

    public static string DefaultPath => Infrastructure.DesktopFiles.File("inventory-notes.json");

    public event EventHandler? Changed;

    public string Get(string? account, string typeKey) =>
        account is not null && Load().TryGetValue(account, out var items) && items.TryGetValue(typeKey, out var note) ? note : string.Empty;

    public void Set(string? account, string typeKey, string note)
    {
        if (account is null) return;
        var all = Load();
        if (!all.TryGetValue(account, out var items)) all[account] = items = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(note))
        {
            if (!items.Remove(typeKey)) return;
            if (items.Count == 0) all.Remove(account);
        }
        else
        {
            if (items.TryGetValue(typeKey, out var existing) && existing == note) return;
            items[typeKey] = note;
        }
        Save(all);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        if (notes is not null) return notes;
        notes = new(StringComparer.Ordinal);
        if (path is null || !File.Exists(path)) return notes;
        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllBytes(path), Json) is { } saved)
                foreach (var (account, items) in saved)
                    if (!string.IsNullOrEmpty(account) && items is not null)
                        notes[account] = items.Where(pair => !string.IsNullOrEmpty(pair.Key) && pair.Value is not null)
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            notes.Clear();
        }
        return notes;
    }

    private void Save(Dictionary<string, Dictionary<string, string>> all)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(all, Json));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
