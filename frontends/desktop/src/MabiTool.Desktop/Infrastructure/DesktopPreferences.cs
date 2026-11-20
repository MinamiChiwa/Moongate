using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using MabiTool.Desktop.Controls.Tables;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>
/// 侧栏的显示选择：“未实现模块”分组的展开状态（缺省收起）与模块入口的顺序（缺省为空，即注册顺序）。
/// 由主窗口读写，偏好文件保存。
/// </summary>
public static class SidebarPreference
{
    public static bool PendingExpanded { get; private set; }

    /// <summary>用户排好的模块入口顺序（模块标识）；不在其中的模块排在所在分组的末尾。</summary>
    public static IReadOnlyList<string> ModuleOrder { get; private set; } = [];

    public static event EventHandler? Changed;

    public static void SetPendingExpanded(bool expanded)
    {
        if (PendingExpanded == expanded) return;
        PendingExpanded = expanded;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetModuleOrder(IReadOnlyList<string> order)
    {
        if (ModuleOrder.SequenceEqual(order)) return;
        ModuleOrder = order.ToList();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>读取偏好时恢复状态，不触发保存。</summary>
    internal static void Restore(bool expanded, IReadOnlyList<string>? order = null)
    {
        PendingExpanded = expanded;
        ModuleOrder = order?.ToList() ?? [];
    }
}

/// <summary>
/// 前端自己的界面偏好：界面语言、主题、动效、用户选定的素材包目录、侧栏分组的展开状态和模块入口的顺序、
/// 各表格的列设置、背包所选账号与显示的列组、背包窗口的位置与尺寸、各页卡片的状态（<see cref="CardStateStore"/>，没有保存过的卡片取各自的缺省值）。
/// </summary>
public sealed record DesktopPreferences(UiLanguage Language, UiThemeChoice Theme, bool? Motion, string? AssetPack = null,
    bool PendingExpanded = false, IReadOnlyList<string>? ModuleOrder = null,
    IReadOnlyDictionary<string, TableSettings>? Tables = null, string? InventoryAccount = null,
    IReadOnlyList<string>? InventoryHiddenGroups = null, WindowPlacement? InventoryWindow = null,
    IReadOnlyDictionary<string, CardState>? Cards = null, WindowPlacement? CombatWindow = null,
    string? RoomsNicknameSource = null)
{
    /// <summary>简体中文、主题跟随系统、动效未选择（跟随系统的动画设置）。</summary>
    public static DesktopPreferences Default { get; } = new(UiLanguage.SimplifiedChinese, UiThemeChoice.System, null);

    /// <summary>界面当前使用的选择。</summary>
    public static DesktopPreferences Current => new(UiText.Language, UiTheme.Choice, UiMotion.Choice, IconRegistry.ChosenDirectory,
        SidebarPreference.PendingExpanded, SidebarPreference.ModuleOrder, TableSettingsStore.All.Count == 0 ? null : TableSettingsStore.All,
        InventoryPreference.Account, InventoryPreference.HiddenGroups.Count == 0 ? null : InventoryPreference.HiddenGroups,
        InventoryPreference.Window,
        CardStateStore.All.Count == 0 ? null : CardStateStore.All, Pages.Combat.CombatDetailPreference.Window,
        Pages.Rooms.RoomsPreference.NicknameSource);
}

/// <summary>
/// 界面偏好的读写。文件缺失或损坏时使用默认值，不影响启动；单项取值无法识别时该项回到默认。
/// </summary>
public sealed class DesktopPreferenceStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string? path;

    private sealed class Document
    {
        public UiLanguage? Language { get; set; }
        public UiThemeChoice? Theme { get; set; }
        /// <summary>用户明确选择的动效：<c>On</c> 或 <c>Reduced</c>；缺省为未选择。</summary>
        public string? MotionChoice { get; set; }
        // 旧版字段：只读取，按当时设置页显示的选择恢复；下次保存时改写成 MotionChoice。
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Motion { get; set; }
        public string? AssetPack { get; set; }
        public bool? PendingExpanded { get; set; }
        public List<string>? ModuleOrder { get; set; }
        public Dictionary<string, TableSettings>? Tables { get; set; }
        public string? InventoryAccount { get; set; }
        public List<string>? InventoryHiddenGroups { get; set; }
        public WindowPlacement? InventoryWindow { get; set; }
        public WindowPlacement? CombatWindow { get; set; }
        public Dictionary<string, CardState>? Cards { get; set; }
        // 旧版字段：只读取，迁移进 Cards 后不再写出。
        public List<string>? DiagnosticsCollapsed { get; set; }
        public List<string>? RoomsCollapsed { get; set; }
        public string? RoomsNicknameSource { get; set; }
    }

    /// <param name="path">保存位置；为空时不读写文件。</param>
    public DesktopPreferenceStore(string? path = null) => this.path = path;

    public static string DefaultPath => DesktopFiles.File("preferences.json");

    public DesktopPreferences Load()
    {
        var fallback = DesktopPreferences.Default;
        if (path is null || !File.Exists(path)) return fallback;
        try
        {
            if (JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json) is not { } document) return fallback;
            return new DesktopPreferences(
                document.Language is { } language && Enum.IsDefined(language) ? language : fallback.Language,
                document.Theme is { } theme && Enum.IsDefined(theme) ? theme : fallback.Theme,
                MotionChoice(document),
                string.IsNullOrWhiteSpace(document.AssetPack) ? null : DesktopFiles.FromToolRoot(document.AssetPack),
                document.PendingExpanded ?? false,
                document.ModuleOrder?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList(),
                document.Tables?.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                string.IsNullOrWhiteSpace(document.InventoryAccount) ? null : document.InventoryAccount,
                document.InventoryHiddenGroups?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList(),
                document.InventoryWindow is { Width: > 0, Height: > 0 } placement ? placement : null,
                Cards(document, LegacyCardHeightsPath),
                document.CombatWindow is { Width: > 0, Height: > 0 } combat ? combat : null,
                document.RoomsNicknameSource);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    private static bool? MotionChoice(Document document) => document.MotionChoice switch
    {
        "On" => true,
        "Reduced" => false,
        _ => document.Motion
    };

    private static readonly string[] LegacyRoomsCards = ["connection", "local", "roster", "diagnostics", "status"];

    // 卡片状态：Cards 里有的以它为准；旧版的诊断页收起分区与合奏页收起卡片在 Cards 没有对应项时迁移进来。
    // 旧版合奏页保存的是完整的收起列表（空列表表示全部展开），五张卡片逐张写明；诊断页只记收起的分区。
    // 旧版 DPS 页两张效果卡片的拖高（列表区高度）在 Cards 里没有布局时换算成半宽档，各列数相同。
    private static Dictionary<string, CardState>? Cards(Document document, string? legacyHeights)
    {
        var cards = new Dictionary<string, CardState>(StringComparer.Ordinal);
        foreach (var (key, state) in document.Cards ?? [])
            if (!string.IsNullOrWhiteSpace(key) && state is not null) cards[key] = state;
        foreach (var id in document.DiagnosticsCollapsed ?? [])
            if (!string.IsNullOrWhiteSpace(id)) cards.TryAdd(CardStateStore.Key("diagnostics", id), new CardState(false));
        if (document.RoomsCollapsed is { } rooms)
            foreach (var id in LegacyRoomsCards) cards.TryAdd(CardStateStore.Key("rooms", id), new CardState(!rooms.Contains(id)));
        foreach (var (legacy, card, chrome) in LegacyEffectCards)
        {
            var key = CardStateStore.Key("combat", card);
            var state = cards.GetValueOrDefault(key) ?? new CardState();
            if (state.Layout is not null || ReadLegacyHeights(legacyHeights) is not { } heights || !heights.TryGetValue(legacy, out var height)) continue;
            var size = Math.Clamp(Controls.CardGrid.RowsFor(height + chrome), 2, 4) switch
            {
                2 => Controls.CardSize.HalfByTwo, 3 => Controls.CardSize.HalfByThree, _ => Controls.CardSize.HalfByFour
            };
            cards[key] = state with { Layout = new[] { 3, 2, 1 }.ToDictionary(columns => columns, _ => new CardSlot(size)) };
        }
        return cards.Count == 0 ? null : cards;
    }

    // 旧版效果卡片的标识、现在的卡片标识，以及卡片里列表区以外的高度（标题行、说明行、内边距；怪物效果多一行目标标签）。
    private static readonly (string Legacy, string Card, double Chrome)[] LegacyEffectCards =
        [("combat.self-effects", "self-effects", 144), ("combat.target-effects", "target-effects", 186)];

    /// <summary>旧版效果卡片拖高的文件，与偏好文件同目录；读取时迁移进 <c>Cards</c>，偏好保存后删除。</summary>
    private string? LegacyCardHeightsPath => path is null ? null : Path.Combine(Path.GetDirectoryName(path)!, "combat-cards.json");

    private static Dictionary<string, double>? ReadLegacyHeights(string? file)
    {
        if (file is null || !File.Exists(file)) return null;
        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllBytes(file)) ?? [])
                .Where(pair => double.IsFinite(pair.Value) && pair.Value > 0).ToDictionary();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(DesktopPreferences preferences)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document
            {
                Language = preferences.Language, Theme = preferences.Theme,
                MotionChoice = preferences.Motion switch { true => "On", false => "Reduced", null => null },
                AssetPack = preferences.AssetPack is { } pack ? DesktopFiles.ToToolRoot(pack) : null, PendingExpanded = preferences.PendingExpanded,
                ModuleOrder = preferences.ModuleOrder is { Count: > 0 } order ? order.ToList() : null,
                Tables = preferences.Tables is { Count: > 0 } tables ? new Dictionary<string, TableSettings>(tables) : null,
                InventoryAccount = preferences.InventoryAccount,
                InventoryHiddenGroups = preferences.InventoryHiddenGroups is { Count: > 0 } groups ? groups.ToList() : null,
                InventoryWindow = preferences.InventoryWindow,
                Cards = preferences.Cards is { Count: > 0 } cards ? new Dictionary<string, CardState>(cards) : null,
                CombatWindow = preferences.CombatWindow,
                RoomsNicknameSource = preferences.RoomsNicknameSource
            }, Json));
            File.Move(temporary, path, true);
            // 旧版拖高已随 Cards 写入偏好文件，旧文件不再需要。
            if (LegacyCardHeightsPath is { } legacy && File.Exists(legacy)) File.Delete(legacy);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>读取并应用到界面，之后语言、主题、动效或素材包选择变化时保存。素材包由启动流程按选择载入。</summary>
    public void ApplyAndTrack(Application application)
    {
        var preferences = Load();
        if (UiText.Language != preferences.Language) UiText.Apply(application, preferences.Language);
        UiTheme.Apply(application, preferences.Theme);
        UiMotion.Restore(preferences.Motion);
        IconRegistry.ChosenDirectory = preferences.AssetPack;
        SidebarPreference.Restore(preferences.PendingExpanded, preferences.ModuleOrder);
        TableSettingsStore.Restore(preferences.Tables);
        InventoryPreference.Restore(preferences.InventoryAccount, preferences.InventoryHiddenGroups, preferences.InventoryWindow);
        CardStateStore.Restore(preferences.Cards);
        // 迁移了旧版效果卡片拖高时立即保存一次，旧文件随之删除。
        if (LegacyCardHeightsPath is { } legacy && File.Exists(legacy)) Save(DesktopPreferences.Current);
        Pages.Rooms.RoomsPreference.Restore(preferences.RoomsNicknameSource);
        Pages.Combat.CombatDetailPreference.Restore(preferences.CombatWindow);
        EventHandler save = (_, _) => Save(DesktopPreferences.Current);
        UiText.LanguageChanged += save;
        UiTheme.Changed += save;
        UiMotion.Changed += save;
        IconRegistry.ChoiceChanged += save;
        SidebarPreference.Changed += save;
        TableSettingsStore.Changed += (_, _) => Save(DesktopPreferences.Current);
        InventoryPreference.Changed += save;
        CardStateStore.Changed += (_, _) => Save(DesktopPreferences.Current);
        CardStateStore.LayoutChanged += (_, _) => Save(DesktopPreferences.Current);
        Pages.Rooms.RoomsPreference.Changed += save;
        Pages.Combat.CombatDetailPreference.Changed += save;
    }
}
