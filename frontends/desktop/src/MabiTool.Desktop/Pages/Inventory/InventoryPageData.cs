using System.Globalization;
using System.Windows.Input;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls.Tables;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Inventory;

/// <summary>一个格子的数量状态：已知数量、存在但数量未知、尚未完整读取（不写 0）、合计里含未读取的格子。</summary>
public enum InventoryCountState { Value, Unknown, NotRead, Partial }

/// <summary>透视表的一个数量格。0 只在该角色该处已完整读取且没有这件道具时出现。</summary>
public sealed record InventoryCount(ulong? Quantity, InventoryCountState State)
{
    public static InventoryCount NotRead { get; } = new(null, InventoryCountState.NotRead);

    public string Text => State switch
    {
        InventoryCountState.Unknown => UiText.Pick("未知", "未知"),
        InventoryCountState.NotRead => UiText.Pick("未读取", "未讀取"),
        _ => (Quantity ?? 0).ToString("N0", CultureInfo.InvariantCulture)
    };

    /// <summary>不是确知的数量（未知、未读取、或合计里含未读取），显示为次要色。</summary>
    public bool Dim => State != InventoryCountState.Value;
}

public sealed record InventoryAccountOption(string Id, string Label);

/// <summary>
/// 账号下拉的显示名。后端在没有取得游戏账号（身份关联帧未到达）时按角色归属，账号 ID 为 <c>character-</c> 加角色 ID：
/// 写明“按角色归属”，公共保管箱的道具记在这个角色名下。只读出账户主角色、还没有游戏账号编号时账号 ID 为 <c>master-</c> 加主角色 ID，
/// 写明“按主角色归户”；取得编号后后端把显示名改为编号。
/// </summary>
public static class InventoryAccountText
{
    public const string CharacterPrefix = "character-";
    public const string MasterPrefix = "master-";

    public static string Label(string accountId, string label) =>
        accountId.StartsWith(CharacterPrefix, StringComparison.Ordinal)
            ? UiText.Pick("按角色归属（公共保管箱记在该角色下）· ", "按角色歸屬（公共保管箱記在該角色下）· ") + accountId[^Math.Min(4, accountId.Length - CharacterPrefix.Length)..]
        : accountId.StartsWith(MasterPrefix, StringComparison.Ordinal) && label == accountId
            ? UiText.Pick("按主角色归户 · ", "按主角色歸戶 · ") + accountId[^Math.Min(4, accountId.Length - MasterPrefix.Length)..]
        : label;
}

/// <summary>账号下的一个角色；名称是游戏原文。</summary>
public sealed record InventoryCharacter(string Id, string Name);

/// <summary>
/// 按道具类型汇总的一行；带实例元数据的乐谱每张一行（<see cref="Score"/> 不为空）。<see cref="RawName"/> 是客户端道具名称原文或乐谱标题，
/// 没有名称时 <see cref="Name"/> 以类型编号显示；<see cref="NameState"/> 是后端名称关联来源的枚举名（为空表示没有名称）。
/// <see cref="Backpack"/> 与 <see cref="Storage"/> 按 <see cref="InventoryPageData.Characters"/> 的顺序排列。
/// </summary>
public sealed record InventoryPivotRow(string TypeKey, string? RawName, string TypeId, string? NameState, string Category, InventoryCount Total,
    InventoryCount Shared, IReadOnlyList<InventoryCount> Backpack, IReadOnlyList<InventoryCount> Storage, InventoryScoreInfo? Score = null)
{
    /// <summary>
    /// 乐谱行的名称是谱名；标题为空时写明“标题未知”，自制谱与系统谱分别标出。没有实例元数据的自制谱（表编号 0、没有表名）按类型合并为一行“自制乐谱”。
    /// </summary>
    public string Name => RawName ?? (Score is not null
        ? Composed ? UiText.Pick("自制乐谱 · 标题未知", "自製樂譜 · 標題未知") : UiText.Pick($"乐谱 #{TypeId} · 标题未知", $"樂譜 #{TypeId} · 標題未知")
        : Composed ? UiText.Pick("自制乐谱", "自製樂譜")
        : UiText.Pick($"类型 #{TypeId} · 名称未收录", $"類型 #{TypeId} · 名稱未收錄"));

    /// <summary>玩家自制乐谱（表编号 0，没有表名）。</summary>
    private bool Composed => Category == "MusicNoteItem" && TypeId == "0";

    /// <summary>类别的显示名（<see cref="InventoryCategoryNames"/>）；筛选、检索、复制与导出都用它。</summary>
    public string CategoryText => InventoryCategoryNames.Display(Category);

    public string NameSource => NameState switch
    {
        null when Score is not null => UiText.Pick("该乐谱实例没有标题", "該樂譜實例沒有標題"),
        null when Composed => UiText.Pick("玩家自制乐谱没有固定名称表，按实例合并为一行", "玩家自製樂譜沒有固定名稱表，按實例合併為一行"),
        null => UiText.Pick("名称目录未收录该类型，以类型编号显示", "名稱目錄未收錄該類型，以類型編號顯示"),
        "Unknown" => UiText.Pick("未知", "未知"),
        "Candidate" => UiText.Pick("候选名称", "候選名稱"),
        "ClientCatalog" => UiText.Pick("客户端名称目录", "用戶端名稱目錄"),
        "VerifiedSource" => UiText.Pick("可信来源", "可信來源"),
        "UserConfirmed" => UiText.Pick("用户已确认", "使用者已確認"),
        "UserCleared" => UiText.Pick("用户已清除", "使用者已清除"),
        "ItemInstance" => UiText.Pick("乐谱实例标题", "樂譜實例標題"),
        var other => other
    };

    /// <summary>名称的悬停提示：名称来源；乐谱行再列出作曲、编曲、可复制、收藏与上锁，读不出的写“未知”。</summary>
    public string Tip => Score is not { } score ? NameSource : string.Join('\n', NameSource,
        UiText.Pick("作曲：", "作曲：") + Text(score.Composer), UiText.Pick("编曲：", "編曲：") + Text(score.Arranger),
        UiText.Pick("可复制：", "可複製：") + Flag(score.CopyingAllowed), UiText.Pick("收藏：", "收藏：") + Flag(score.Favorite),
        UiText.Pick("上锁：", "上鎖：") + Flag(score.Locked));

    /// <summary>搜索命中谱名之外的作曲与编曲。</summary>
    public bool Matches(string search) => Score is { } score &&
        (score.Composer?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true ||
         score.Arranger?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true);

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? UiText.Pick("未知", "未知") : value;
    private static string Flag(bool? value) => value switch { true => UiText.Pick("是", "是"), false => UiText.Pick("否", "否"), null => UiText.Pick("未知", "未知") };
}

public sealed record CoverageRow(string Label, string State, bool Complete);

/// <summary>
/// 背包同步状态一行。背包是全局常驻抓包的兴趣方：抓包运行时自动同步；游戏只在登录角色、换频道或进图时下发完整背包，
/// 之后的增减随抓包实时更新。没有完整背包时写明等待，不显示 0。
/// </summary>
public static class InventorySyncText
{
    public static string Status(bool connected, CaptureStatusView capture, InventoryWorkspace? workspace)
    {
        if (!connected) return UiText.Pick("未连接后端", "未連接後端");
        var at = workspace?.LastObservedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (workspace is { Current: true })
            return UiText.Pick($"已同步 {at}，随全局抓包自动更新", $"已同步 {at}，隨全域抓包自動更新")
                   + (workspace.Quality.KnownValue == ObservationQuality.Confirmed ? string.Empty : UiText.Pick("（部分类别不完整）", "（部分類別不完整）"));
        var previous = at is null ? string.Empty : UiText.Pick($"；下表为 {at} 同步的结果", $"；下表為 {at} 同步的結果");
        if (capture.Running)
            return UiText.Pick("同步中：等待游戏下发完整背包（换频道或进图时同步）", "同步中：等待遊戲下發完整背包（換頻道或進圖時同步）") + previous;
        return (workspace is null ? UiText.Pick("未读取", "未讀取") : UiText.Pick("未同步", "未同步")) + " · " + CaptureText.Status(capture) + previous;
    }
}

/// <summary>
/// 背包页数据。<see cref="Status"/> 是同步状态一行：背包随全局常驻抓包自动同步，没有开始/停止操作；
/// 状态为未读取、同步中（等待游戏下发完整背包）、已同步（时间）或未同步（全局抓包未运行及原因）。
/// <see cref="Summary"/> 是概况里角色行之后的固定行（道具种数，未读取时写“未读取”）；概况的角色与三项资源由页面模型按所选角色从 <see cref="Resources"/> 取。
/// <see cref="Resources"/> 是资源视图换成的行（见 <see cref="InventoryResourceText"/>），后端未提供时为空。
/// </summary>
public sealed record InventoryPageData(string Status, IReadOnlyList<KeyValue> Summary,
    IReadOnlyList<InventoryAccountOption> Accounts, string? AccountId, IReadOnlyList<InventoryCharacter> Characters,
    IReadOnlyList<InventoryPivotRow> Rows, IReadOnlyList<CoverageRow> Coverage, IReadOnlyList<KeyValue> Catalog,
    IReadOnlyList<InventoryResourceRow>? Resources = null, string? CurrentCharacterId = null);

/// <summary>“角色资源”组的一个角色选项；角色列表与透视表的角色列同一来源（<see cref="InventoryPageData.Characters"/>）。</summary>
public sealed record ResourceCharacterChoice(string Id, string Name, bool Active, ICommand Command);

/// <summary>“显示”里的一个列组开关（可同时选中多项）；按值比较，刷新时内容不变的开关容器保留（StableItems）。</summary>
public sealed record GroupToggle(string Id, string Label, bool Shown, ICommand Command);

/// <summary>
/// 背包页模型：账号选择、列组开关与搜索是界面上的显示选择；透视表的列由账号下的角色生成，
/// 备注列的内容来自本机备注文件。背包窗口用同一后端另建一个模型，列设置、备注、账号与列组通过共用的存储同步。
/// </summary>
public sealed class InventoryPageModel : BackendPageModel<InventoryPageData>
{
    private readonly InventoryNoteStore notes;
    private string search = string.Empty;
    private IReadOnlyList<TableColumn> columns = [];
    private string columnKey = string.Empty;

    /// <summary>切走后保留：数据来自后端适配层常驻刷新，没有自有计时器。</summary>
    protected override bool Cacheable => true;

    public InventoryPageModel(IDesktopBackend backend, InventoryNoteStore? notes = null) : base(backend, source => source.GetInventory())
    {
        this.notes = notes ?? InventoryNoteStore.Shared;
        ToggleGroupCommand = new RelayCommand(id =>
        {
            if (id is string group) InventoryPreference.SetGroupShown(group, InventoryPreference.HiddenGroups.Contains(group));
        });
        this.notes.Changed += OnNotesChanged;
        InventoryPreference.Changed += OnPreferenceChanged;
        // 上次选过的账号与后端当前所选不同时，请后端按它读取；后端没有这个账号时仍取默认账号。
        if (InventoryPreference.Account is { } saved && saved != Data.AccountId) Send("inventory.account", saved);
    }

    public ICommand ToggleGroupCommand { get; }

    // ---- 资源区：“账户资源”跟随账号下拉；“角色资源”按所选账户下的角色分开，默认当前登录角色。未读取的各组折叠。 ----
    // 概况的金币、银币、魔族貢品与资源区读同一份 Data.Resources、跟随同一个所选角色；角色资源组不重复列出这三项。
    private bool unreadAccountExpanded, unreadCharacterExpanded;
    private string? resourceCharacter;
    private IEnumerable<InventoryResourceRow> AccountRows => (Data.Resources ?? []).Where(row => row.Account);
    private IEnumerable<InventoryResourceRow> CharacterRows => (Data.Resources ?? []).Where(row => !row.Account && !row.Summary && row.CharacterId == ResourceCharacter);
    /// <summary>概况的三行（金币、银币、魔族貢品），始终三行；所选角色没有读数时为未读取。</summary>
    public IReadOnlyList<InventoryResourceRow> SummaryResources => InventoryResourceText.Summary(Data.Resources ?? [], ResourceCharacter);
    /// <summary>概况的角色行：资源区所选角色的名称，是当前登录角色时注明。</summary>
    public string SummaryCharacter => Data.Characters.FirstOrDefault(c => c.Id == ResourceCharacter) is { } character
        ? character.Name + (character.Id == Data.CurrentCharacterId ? UiText.Pick("（当前登录）", "（目前登入）") : string.Empty)
        : UiText.Pick("未读取", "未讀取");
    public bool HasResources => Data.Resources is { Count: > 0 };
    public IReadOnlyList<InventoryResourceRow> AccountResources => AccountRows.Where(row => row.Observed).ToList();
    public IReadOnlyList<InventoryResourceRow> UnreadAccountResources => AccountRows.Where(row => !row.Observed).ToList();
    public string UnreadAccountResourcesText => UiText.Pick($"未读取 {UnreadAccountResources.Count} 项", $"未讀取 {UnreadAccountResources.Count} 項");
    public bool HasUnreadAccountResources => UnreadAccountResources.Count > 0;
    public bool UnreadAccountExpanded { get => unreadAccountExpanded; private set => Set(ref unreadAccountExpanded, value); }
    public ICommand ToggleUnreadAccountCommand => toggleAccount ??= new RelayCommand(_ => UnreadAccountExpanded = !unreadAccountExpanded);
    private ICommand? toggleAccount;
    /// <summary>“角色资源”显示的角色：用户选过且仍在本账户的角色，否则当前登录角色，否则第一个角色。</summary>
    public string? ResourceCharacter => Data.Characters.Any(c => c.Id == resourceCharacter) ? resourceCharacter
        : Data.Characters.Any(c => c.Id == Data.CurrentCharacterId) ? Data.CurrentCharacterId : Data.Characters.FirstOrDefault()?.Id;
    public IReadOnlyList<ResourceCharacterChoice> ResourceCharacters => Data.Characters
        .Select(c => new ResourceCharacterChoice(c.Id, c.Name, c.Id == ResourceCharacter, SelectResourceCharacterCommand)).ToList();
    public bool HasResourceCharacters => Data.Characters.Count > 0;
    public ICommand SelectResourceCharacterCommand => selectCharacter ??= new RelayCommand(id =>
    {
        if (id is not string value || value == resourceCharacter) return;
        resourceCharacter = value;
        foreach (var name in new[] { nameof(ResourceCharacter), nameof(ResourceCharacters), nameof(CharacterResources), nameof(UnreadCharacterResources),
                     nameof(UnreadCharacterResourcesText), nameof(HasUnreadCharacterResources), nameof(NoCharacterResources),
                     nameof(SummaryResources), nameof(SummaryCharacter) }) Raise(name);
    });
    private ICommand? selectCharacter;
    public IReadOnlyList<InventoryResourceRow> CharacterResources => CharacterRows.Where(row => row.Observed).ToList();
    public bool NoCharacterResources => HasResources && CharacterResources.Count == 0;
    public IReadOnlyList<InventoryResourceRow> UnreadCharacterResources => CharacterRows.Where(row => !row.Observed).ToList();
    public string UnreadCharacterResourcesText => UiText.Pick($"未读取 {UnreadCharacterResources.Count} 项", $"未讀取 {UnreadCharacterResources.Count} 項");
    public bool HasUnreadCharacterResources => UnreadCharacterResources.Count > 0;
    public bool UnreadCharacterExpanded { get => unreadCharacterExpanded; private set => Set(ref unreadCharacterExpanded, value); }
    public ICommand ToggleUnreadCharacterCommand => toggleCharacter ??= new RelayCommand(_ => UnreadCharacterExpanded = !unreadCharacterExpanded);
    private ICommand? toggleCharacter;

    /// <summary>后端能力表说明该主操作不可用的原因；可用时为空。</summary>
    public string CapabilityNotice => Backend.GetCapabilityNotice(CapabilityIds.InventoryFull) ?? string.Empty;
    public bool HasCapabilityNotice => CapabilityNotice.Length > 0;

    public bool HasAccounts => Data.Accounts.Count > 0;
    public bool NoAccounts => !HasAccounts;

    private IReadOnlyList<InventoryAccountOption> accounts = [];
    private string? pendingAccount;

    /// <summary>
    /// 下拉框的账号列表。内容不变时返回同一个列表，数据重读不会让下拉框重新选择并回写旧账号。
    /// </summary>
    public IReadOnlyList<InventoryAccountOption> Accounts =>
        accounts.SequenceEqual(Data.Accounts) ? accounts : accounts = Data.Accounts;

    /// <summary>
    /// 下拉框所选账号；改选时记入偏好并请后端读取该账号。后端读到该账号之前仍显示刚选的账号。
    /// </summary>
    public InventoryAccountOption? SelectedAccount
    {
        get
        {
            if (pendingAccount == Data.AccountId) pendingAccount = null;
            var id = pendingAccount is not null && Accounts.Any(account => account.Id == pendingAccount) ? pendingAccount : Data.AccountId;
            return Accounts.FirstOrDefault(account => account.Id == id);
        }
        set
        {
            if (value is null || value.Id == (pendingAccount ?? Data.AccountId)) return;
            pendingAccount = value.Id == Data.AccountId ? null : value.Id;
            InventoryPreference.SetAccount(value.Id);
            Send("inventory.account", value.Id);
        }
    }

    public string Search
    {
        get => search;
        set
        {
            if (!Set(ref search, value)) return;
            Raise(nameof(Rows));
        }
    }

    public IReadOnlyList<GroupToggle> Groups =>
    [
        new(InventoryPreference.StorageGroup, UiText.Pick("角色保管箱", "角色保管箱"), !InventoryPreference.HiddenGroups.Contains(InventoryPreference.StorageGroup), ToggleGroupCommand),
        new(InventoryPreference.BackpackGroup, UiText.Pick("背包", "背包"), !InventoryPreference.HiddenGroups.Contains(InventoryPreference.BackpackGroup), ToggleGroupCommand),
        new(InventoryPreference.SharedGroup, UiText.Pick("公共保管箱", "公共保管箱"), !InventoryPreference.HiddenGroups.Contains(InventoryPreference.SharedGroup), ToggleGroupCommand)
    ];

    /// <summary>搜索词在名称、类别、备注与乐谱的作曲、编曲里查找（不区分大小写）；只在已读取的行里筛，不改变后端数据。</summary>
    public IReadOnlyList<InventoryPivotRow> Rows => Data.Rows
        .Where(row => search.Length == 0 || row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                      || row.CategoryText.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                      || Note(row).Contains(search, StringComparison.CurrentCultureIgnoreCase) || row.Matches(search))
        .ToList();

    /// <summary>透视表的列：名称、名称来源（默认隐藏）、类别、合计、公共保管箱、每个角色的角色保管箱与背包、备注。</summary>
    public IReadOnlyList<TableColumn> Columns
    {
        get
        {
            var hidden = InventoryPreference.HiddenGroups;
            var key = string.Join('\u0001', Data.Characters.Select(c => c.Id + "\u0002" + c.Name)) + "|" + string.Join(',', hidden.Order());
            if (key == columnKey) return columns;
            columnKey = key;
            return columns = BuildColumns(Data.Characters, hidden);
        }
    }

    public string ExportFileName
    {
        get
        {
            var label = SelectedAccount?.Label ?? string.Empty;
            var date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            return UiText.Pick("背包", "背包") + (label.Length > 0 ? "-" + label : string.Empty) + "-" + date;
        }
    }

    public string Note(InventoryPivotRow row) => notes.Get(Data.AccountId, row.TypeKey);

    /// <summary>背包窗口用的模型：同一后端、同一份备注。</summary>
    public InventoryPageModel Companion() => new(Backend, notes);

    private List<TableColumn> BuildColumns(IReadOnlyList<InventoryCharacter> characters, IReadOnlyList<string> hidden)
    {
        static InventoryPivotRow R(object row) => (InventoryPivotRow)row;
        var list = new List<TableColumn>
        {
            new() { Id = "name", HeaderKey = "I.ColName", Path = nameof(InventoryPivotRow.Name), Width = 240, MinWidth = 120, Fill = true, CanHide = false,
                ToolTip = row => R(row).Tip, CellClasses = "strong" },
            new() { Id = "source", HeaderKey = "I.ColNameSource", Value = row => R(row).NameSource, Width = 160, HiddenByDefault = true },
            new() { Id = "category", HeaderKey = "I.ColCategory", Value = row => R(row).CategoryText, Width = 110 },
            new() { Id = "total", HeaderKey = "I.ColTotal", Kind = ColumnKind.Number, Width = 96,
                Value = row => R(row).Total.Text, Dim = row => R(row).Total.Dim }
        };
        if (!hidden.Contains(InventoryPreference.SharedGroup))
            list.Add(new() { Id = "shared", HeaderKey = "I.AccountStorage", Kind = ColumnKind.Number, Width = 112,
                Value = row => R(row).Shared.Text, Dim = row => R(row).Shared.Dim });
        if (!hidden.Contains(InventoryPreference.StorageGroup))
            list.AddRange(characters.Select((character, index) => new TableColumn
            {
                Id = "storage:" + character.Id, Header = character.Name, GroupKey = "I.CharacterStorage", Kind = ColumnKind.Number, Width = 104,
                Value = row => Cell(R(row).Storage, index).Text, Dim = row => Cell(R(row).Storage, index).Dim
            }));
        if (!hidden.Contains(InventoryPreference.BackpackGroup))
            list.AddRange(characters.Select((character, index) => new TableColumn
            {
                Id = "backpack:" + character.Id, Header = character.Name, GroupKey = "I.Backpack", Kind = ColumnKind.Number, Width = 104,
                Value = row => Cell(R(row).Backpack, index).Text, Dim = row => Cell(R(row).Backpack, index).Dim
            }));
        list.Add(new()
        {
            Id = "note", HeaderKey = "I.ColNote", Width = 180, Editable = true, Renamable = true,
            Value = row => Note(R(row)), Commit = (row, text) => notes.Set(Data.AccountId, R(row).TypeKey, text)
        });
        return list;
    }

    private static InventoryCount Cell(IReadOnlyList<InventoryCount> cells, int index) => index < cells.Count ? cells[index] : InventoryCount.NotRead;

    public override void Dispose()
    {
        notes.Changed -= OnNotesChanged;
        InventoryPreference.Changed -= OnPreferenceChanged;
        base.Dispose();
    }

    private void OnNotesChanged(object? sender, EventArgs e) => Raise(nameof(Rows));

    private void OnPreferenceChanged(object? sender, EventArgs e)
    {
        Raise(nameof(Groups));
        Raise(nameof(Columns));
    }
}
