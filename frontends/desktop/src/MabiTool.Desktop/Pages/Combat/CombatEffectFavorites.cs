using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using MabiTool.Desktop.Backend;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>“已提取到的 Buff”目录里的一项：效果编号、名称（游戏原文，未解析时为“未知效果 #编号”）与极性。</summary>
public sealed record EffectCatalogEntry(string Id, string Name, EffectPolarity Polarity);

/// <summary>
/// 收藏层里的一行：编号、名称、极性、是否已收藏，以及客户端 Buff 表的描述（没有时为空）、是否本机观测到过、名称是否与其他项重名。
/// 重名时第二行显示描述摘要（没有描述时写“无描述”）以区分；悬停提示是名称、编号与完整描述。图标与效果卡片同一种五边形。
/// </summary>
public sealed record FavoriteEntryView(string Id, string Name, EffectPolarity Polarity, bool Favorite)
{
    private readonly EffectRow icon = new(string.Empty, Name, string.Empty, string.Empty, string.Empty, Id, string.Empty, Polarity);
    public string? Description { get; init; }
    public bool Observed { get; init; }
    public bool Duplicate { get; init; }
    public IBrush Fill => icon.Fill;
    public double Angle => icon.Angle;
    public string Label => $"{Name}（{Id}）";
    /// <summary>重名时的第二行：描述的第一行（至多 40 字）。</summary>
    public string Summary => !Duplicate ? string.Empty
        : Description is { Length: > 0 } text ? Shorten(text.Split('\n')[0], 40) : Localization.UiText.Pick("无描述", "無描述");
    public bool HasSummary => Duplicate;
    public string Tip => Description is { Length: > 0 } text ? Label + "\n" + text : Label;
    public string StarText => Favorite ? "★" : "☆";
    public string StarTip => Favorite ? Localization.UiText.Pick("取消收藏", "取消收藏") : Localization.UiText.Pick("收藏", "收藏");
    /// <summary>收藏或取消这一项；收藏时把名称与极性一并记进目录（从未观测到的效果也能在收藏浮窗里显示名称）。</summary>
    public System.Windows.Input.ICommand ToggleCommand => new Infrastructure.RelayCommand(_ => CombatEffectFavorites.Toggle(this));

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>收藏层一次搜索的结果：列出的行（至多上限）与符合条件的总数。</summary>
public sealed record FavoriteSearchResult(IReadOnlyList<FavoriteEntryView> Items, int Total);

/// <summary>
/// DPS 模块的 Buff 收藏与“只显示收藏”筛选：整个前端只有这一份状态，主页面、DPS 明细窗口与浮窗都读它。
/// 后端适配层在 <c>GetCombat</c> 里统一套用筛选并顺带记下本机观测到的效果（“已提取到的 Buff”目录，至多 <see cref="MaximumCatalog"/> 项），
/// 页面与浮窗只显示结果，不各自筛选。收藏与筛选属于界面的显示选择（与表格列设置、背包备注同类），保存在前端文件
/// 工具根下的 <c>desktop\combat-effects.json</c>；应用启动时由 <see cref="Use"/> 换成该文件，测试与样本只在内存里。
/// 收藏层的可选范围是客户端 Buff 表（<see cref="ClientBuffList"/>）与这份目录的并集；收藏一个从未观测到的效果时，
/// 名称与极性取自客户端表并写进目录（收藏的项不受目录上限限制），收藏浮窗与效果卡片据此显示名称。
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

    /// <summary>一项收藏被加入（第二个参数为真）或取消；收藏浮窗据此把新收藏追加到“收藏浮窗 1”、把取消的项从两个收藏浮窗移除。</summary>
    public static event Action<string, bool>? Toggled;

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
            foreach (var entry in document.Catalog ?? [])
                if (entry is { Id.Length: > 0, Name.Length: > 0 } && (catalog.Count < MaximumCatalog || favorites.Contains(entry.Id))) catalog[entry.Id] = entry;
            SelfOnly = document.SelfOnly; TargetOnly = document.TargetOnly;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
    }

    public static bool IsFavorite(string? id) => id is not null && favorites.Contains(id);

    public static void Toggle(string id) => Toggle(id, null, EffectPolarity.Unknown);

    /// <summary>收藏或取消收藏层的一行；收藏时名称与极性记进目录（目录里还没有这一项，或只有未解析的名称、未知的极性时）。</summary>
    public static void Toggle(FavoriteEntryView entry) => Toggle(entry.Id, entry.Name, entry.Polarity);

    private static void Toggle(string id, string? name, EffectPolarity polarity)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var added = !favorites.Remove(id);
        if (added) favorites.Add(id);
        var named = added && name is { Length: > 0 } && Remember(id, name, polarity);
        Version++;
        Save();
        if (named) CatalogChanged?.Invoke(null, EventArgs.Empty);
        Toggled?.Invoke(id, added);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 客户端 Buff 表到达时补齐收藏的名称与极性：收藏里目录没有的、或只有未解析名称的项取客户端表原文。
    /// </summary>
    public static void Learn(ClientBuffList client)
    {
        if (!client.Ready || favorites.Count == 0) return;
        var changed = false;
        foreach (var entry in client.Entries)
            if (favorites.Contains(entry.Id)) changed |= Remember(entry.Id, entry.Name, entry.Polarity);
        if (!changed) return;
        Version++;
        Save(); CatalogChanged?.Invoke(null, EventArgs.Empty); Changed?.Invoke(null, EventArgs.Empty);
    }

    // 已解析的名称不被覆盖；极性只在目录里是未知时补上。
    private static bool Remember(string id, string name, EffectPolarity polarity)
    {
        if (Unresolved(id, name)) return false;
        if (catalog.TryGetValue(id, out var known))
        {
            var rename = Unresolved(id, known.Name);
            var repolar = known.Polarity == EffectPolarity.Unknown && polarity != EffectPolarity.Unknown;
            if (!rename && !repolar) return false;
            catalog[id] = new(id, rename ? name : known.Name, repolar ? polarity : known.Polarity);
            return true;
        }
        catalog[id] = new(id, name, polarity);
        return true;
    }

    /// <summary>名称是否是未解析时的占位（“未知效果 #编号”“名称读取中 #编号”，简繁两种界面语言）。</summary>
    internal static bool Unresolved(string id, string name) =>
        name.EndsWith(" #" + id, StringComparison.Ordinal)
        && (name.StartsWith("未知效果", StringComparison.Ordinal) || name.StartsWith("名称读取中", StringComparison.Ordinal) || name.StartsWith("名稱讀取中", StringComparison.Ordinal));

    /// <summary>目录里记下的极性；没有这一项时为未知。</summary>
    public static EffectPolarity PolarityOf(string id) => catalog.TryGetValue(id, out var entry) ? entry.Polarity : EffectPolarity.Unknown;

    /// <summary>目录里记下的名称；没有这一项时为空。</summary>
    public static string? NameOf(string id) => catalog.TryGetValue(id, out var entry) ? entry.Name : null;

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

    /// <summary>收藏层一次至多列出的行数；符合条件的更多时写明总数，请用户输入更多文字缩小范围。</summary>
    public const int SearchLimit = 200;

    /// <summary>按名称或编号查找（见 <see cref="Find"/>），只取列出的行。</summary>
    public static IReadOnlyList<FavoriteEntryView> Search(string? query, ClientBuffList? client = null, int limit = SearchLimit) =>
        Find(query, client, limit).Items;

    /// <summary>
    /// 在客户端 Buff 表与本机观测目录的并集里按名称或编号查找。名称简繁不分、不分大小写地包含搜索词即符合（<see cref="ChineseSearch.Fold"/>），
    /// 编号包含搜索词也符合。同一编号两边都有时：名称取目录里已解析的原文（目录只有“未知效果 #编号”时取客户端表），极性取已知的一方，描述取客户端表。
    /// 顺序：已收藏、本机观测到过、其余；组内按正面、负面、其他，再按名称与编号。重名（并集里有同名的其他编号）的行标出以显示描述摘要。至多 <paramref name="limit"/> 行。
    /// </summary>
    public static FavoriteSearchResult Find(string? query, ClientBuffList? client = null, int limit = SearchLimit)
    {
        var text = query?.Trim() ?? string.Empty;
        var folded = ChineseSearch.Fold(text);
        var merged = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        if (client is { Ready: true })
            foreach (var entry in client.Entries)
                merged.TryAdd(entry.Id, new(entry.Id, entry.Name, entry.SearchKey, entry.Polarity, entry.Description, false));
        foreach (var entry in catalog.Values)
        {
            if (merged.TryGetValue(entry.Id, out var known))
            {
                var useCatalog = !Unresolved(entry.Id, entry.Name);
                merged[entry.Id] = known with
                {
                    Name = useCatalog ? entry.Name : known.Name, Key = useCatalog ? Folded(entry.Name) : known.Key,
                    Polarity = entry.Polarity != EffectPolarity.Unknown ? entry.Polarity : known.Polarity, Observed = true
                };
            }
            else merged[entry.Id] = new(entry.Id, entry.Name, Folded(entry.Name), entry.Polarity, null, true);
        }
        var names = merged.Values.GroupBy(item => item.Name, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var matches = merged.Values
            .Where(item => text.Length == 0 || item.Key.Contains(folded, StringComparison.Ordinal) || item.Id.Contains(text, StringComparison.Ordinal))
            .ToList();
        var items = matches
            .OrderBy(item => favorites.Contains(item.Id) ? 0 : item.Observed ? 1 : 2)
            .ThenBy(item => item.Polarity switch { EffectPolarity.Positive => 0, EffectPolarity.Negative => 1, _ => 2 })
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Id.Length).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => new FavoriteEntryView(item.Id, item.Name, item.Polarity, favorites.Contains(item.Id))
            {
                Description = item.Description, Observed = item.Observed, Duplicate = names.Contains(item.Name)
            })
            .ToList();
        return new(items, matches.Count);
    }

    // 目录名称的搜索归一化缓存（目录至多几千项，名称不变时不再重复转换）。
    private static readonly Dictionary<string, string> folds = new(StringComparer.Ordinal);
    private static string Folded(string name)
    {
        if (folds.TryGetValue(name, out var value)) return value;
        if (folds.Count >= MaximumCatalog * 2) folds.Clear();
        return folds[name] = ChineseSearch.Fold(name);
    }

    private sealed record Candidate(string Id, string Name, string Key, EffectPolarity Polarity, string? Description, bool Observed);

    /// <summary>
    /// 记下这份数据里的效果（已解析的名称覆盖未解析的），排好顺序（收藏在前、按首次出现），再按“只显示收藏”筛选自身与怪物效果。
    /// 主页面、明细窗口与浮窗拿到的都是这一份结果。
    /// </summary>
    public static CombatPageData Apply(CombatPageData data)
    {
        Record(data.SelfEffects.Concat(data.TargetEffects).Concat(data.AimEffects));
        // 后端没能解析名称（名称表读取中或不可用）而目录里有原文时显示目录里的原文（例如收藏时从客户端表记下的名称）。
        data = data with { SelfEffects = Named(data.SelfEffects), TargetEffects = Named(data.TargetEffects), AimEffects = Named(data.AimEffects) };
        // 顺序与倒计时总时长按全部效果记住（筛选之前），收藏的排在前面。
        var allSelf = CombatEffectOrder.Arrange(data.SelfEffects, "self");
        var allTarget = CombatEffectOrder.Arrange(data.TargetEffects, "target");
        // 收藏浮窗不受“只显示收藏”影响：自身取筛选前的全部，目标取当前攻击目标的全部（另记倒计时总时长）。
        data = data with { SelfEffects = allSelf, TargetEffects = allTarget, AllSelfEffects = allSelf, AimEffects = data.AimEffects.Count > 0 ? CombatEffectOrder.Arrange(data.AimEffects, "aim") : data.AimEffects };
        MabiTool.Desktop.Overlay.FavoriteEffectTracker.Observe(data);
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

    private static IReadOnlyList<EffectRow> Named(IReadOnlyList<EffectRow> rows)
    {
        // 已解析的名称悬停提示为空或与名称相同；未解析的才换成目录里的原文。
        string? Known(EffectRow row) => row.EffectId is { } id && row.Tip.Length > 0 && row.Tip != row.Name
            && catalog.TryGetValue(id, out var entry) && entry.Name != row.Name && !Unresolved(id, entry.Name) ? entry.Name : null;
        if (!rows.Any(row => Known(row) is not null)) return rows;
        return rows.Select(row => Known(row) is { } name ? row with { Name = name, Tip = name } : row).ToList();
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
