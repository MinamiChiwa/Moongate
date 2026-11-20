using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>“已提取到的 Buff”目录里的一项：效果编号、名称（游戏原文，未解析时为“未知效果 #编号”）与极性。</summary>
public sealed record EffectCatalogEntry(string Id, string Name, EffectPolarity Polarity);

/// <summary>收藏层里的一行：目录项与是否已收藏；图标与效果卡片同一种五边形。</summary>
public sealed record FavoriteEntryView(string Id, string Name, EffectPolarity Polarity, bool Favorite)
{
    private readonly EffectRow icon = new(string.Empty, Name, string.Empty, string.Empty, string.Empty, Id, string.Empty, Polarity);
    public IBrush Fill => icon.Fill;
    public double Angle => icon.Angle;
    public string Label => $"{Name}（{Id}）";
    public string StarText => Favorite ? "★" : "☆";
    public string StarTip => Favorite ? Localization.UiText.Pick("取消收藏", "取消收藏") : Localization.UiText.Pick("收藏", "收藏");
    /// <summary>收藏或取消这一项；收藏状态只有一份，命令不依赖界面上下文。</summary>
    public System.Windows.Input.ICommand ToggleCommand => new Infrastructure.RelayCommand(_ => CombatEffectFavorites.Toggle(Id));
}

/// <summary>
/// DPS 模块的 Buff 收藏与“只显示收藏”筛选：整个前端只有这一份状态，主页面、DPS 明细窗口与浮窗都读它。
/// 后端适配层在 <c>GetCombat</c> 里统一套用筛选并顺带记下本机观测到的效果（“已提取到的 Buff”目录，至多 <see cref="MaximumCatalog"/> 项），
/// 页面与浮窗只显示结果，不各自筛选。收藏与筛选属于界面的显示选择（与表格列设置、背包备注同类），保存在前端文件
/// 工具根下的 <c>desktop\combat-effects.json</c>；应用启动时由 <see cref="Use"/> 换成该文件，测试与样本只在内存里。
/// </summary>
public static class CombatEffectFavorites
{
    public const int MaximumCatalog = 4096;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly HashSet<string> favorites = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, EffectCatalogEntry> catalog = new(StringComparer.Ordinal);
    private static string? path;

    /// <summary>收藏或筛选变化（在界面线程上）；页面与浮窗据此重读战斗数据。</summary>
    public static event EventHandler? Changed;

    /// <summary>目录新增或更名；收藏层据此刷新列表。</summary>
    public static event EventHandler? CatalogChanged;

    /// <summary>收藏或筛选每变化一次加一；战斗数据的映射缓存据此失效。</summary>
    public static int Version { get; private set; }

    public static bool SelfOnly { get; private set; }
    public static bool TargetOnly { get; private set; }
    public static IReadOnlyCollection<string> Favorites => favorites;
    public static int CatalogCount => catalog.Count;

    public static string DefaultPath => Infrastructure.DesktopFiles.File("combat-effects.json");

    private sealed class Document
    {
        public List<string>? Favorites { get; set; }
        public bool SelfOnly { get; set; }
        public bool TargetOnly { get; set; }
        public List<EffectCatalogEntry>? Catalog { get; set; }
    }

    /// <summary>换用保存位置并读取；文件缺失或损坏时为空。<paramref name="file"/> 为空时只在内存里（测试）。</summary>
    public static void Use(string? file)
    {
        path = file;
        Version++;
        favorites.Clear(); catalog.Clear(); SelfOnly = TargetOnly = false;
        if (file is null || !File.Exists(file)) return;
        try
        {
            if (JsonSerializer.Deserialize<Document>(File.ReadAllBytes(file), Json) is not { } document) return;
            foreach (var id in document.Favorites ?? []) if (!string.IsNullOrWhiteSpace(id)) favorites.Add(id);
            foreach (var entry in (document.Catalog ?? []).Take(MaximumCatalog))
                if (entry is { Id.Length: > 0, Name.Length: > 0 }) catalog[entry.Id] = entry;
            SelfOnly = document.SelfOnly; TargetOnly = document.TargetOnly;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
    }

    public static bool IsFavorite(string? id) => id is not null && favorites.Contains(id);

    public static void Toggle(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!favorites.Remove(id)) favorites.Add(id);
        Version++;
        Save(); Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetSelfOnly(bool value)
    {
        if (SelfOnly == value) return;
        SelfOnly = value; Version++; Save(); Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetTargetOnly(bool value)
    {
        if (TargetOnly == value) return;
        TargetOnly = value; Version++; Save(); Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>按名称或编号查找目录（不分大小写）；收藏的排在前面，至多 <paramref name="limit"/> 项。</summary>
    public static IReadOnlyList<FavoriteEntryView> Search(string? query, int limit = 200)
    {
        var text = query?.Trim() ?? string.Empty;
        return catalog.Values
            .Where(entry => text.Length == 0 || entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || entry.Id.Contains(text, StringComparison.Ordinal))
            .OrderByDescending(entry => favorites.Contains(entry.Id))
            .ThenBy(entry => entry.Polarity switch { EffectPolarity.Positive => 0, EffectPolarity.Negative => 1, _ => 2 })
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(entry => new FavoriteEntryView(entry.Id, entry.Name, entry.Polarity, favorites.Contains(entry.Id)))
            .ToList();
    }

    /// <summary>
    /// 记下这份数据里的效果（已解析的名称覆盖未解析的），排好顺序（收藏在前、按首次出现），再按“只显示收藏”筛选自身与怪物效果。
    /// 主页面、明细窗口与浮窗拿到的都是这一份结果。
    /// </summary>
    public static CombatPageData Apply(CombatPageData data)
    {
        Record(data.SelfEffects.Concat(data.TargetEffects));
        // 顺序与倒计时总时长按全部效果记住（筛选之前），收藏的排在前面。
        var allSelf = CombatEffectOrder.Arrange(data.SelfEffects, "self");
        var allTarget = CombatEffectOrder.Arrange(data.TargetEffects, "target");
        data = data with { SelfEffects = allSelf, TargetEffects = allTarget };
        var self = SelfOnly ? allSelf.Where(row => row.Favorite).ToList() : allSelf;
        var target = TargetOnly ? allTarget.Where(row => row.Favorite).ToList() : allTarget;
        if (!SelfOnly && !TargetOnly) return data;
        return data with
        {
            SelfEffects = self, TargetEffects = target,
            SelfFavoritesEmpty = SelfOnly && self.Count == 0 && data.SelfEffects.Count > 0,
            TargetFavoritesEmpty = TargetOnly && target.Count == 0 && data.TargetEffects.Count > 0
        };
    }

    private static void Record(IEnumerable<EffectRow> rows)
    {
        var changed = false;
        foreach (var row in rows)
        {
            if (row.EffectId is not { Length: > 0 } id) continue;
            // 名称已解析时悬停提示为空或与名称相同；未解析的名称只在目录里还没有这一项时记下。
            var resolved = row.Tip.Length == 0 || row.Tip == row.Name;
            if (catalog.TryGetValue(id, out var known))
            {
                if (known.Polarity == row.Polarity && (known.Name == row.Name || !resolved)) continue;
                catalog[id] = known with { Name = resolved ? row.Name : known.Name, Polarity = row.Polarity == EffectPolarity.Unknown ? known.Polarity : row.Polarity };
                changed = true;
                continue;
            }
            if (catalog.Count >= MaximumCatalog) continue;
            catalog[id] = new(id, row.Name, row.Polarity);
            changed = true;
        }
        if (!changed) return;
        Save(); CatalogChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Save()
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document
            {
                Favorites = favorites.Order(StringComparer.Ordinal).ToList(), SelfOnly = SelfOnly, TargetOnly = TargetOnly,
                Catalog = catalog.Values.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToList()
            }, Json));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
