using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Settings;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

public sealed partial class SampleBackend
{
    private static readonly (string Id, string Title, string Tag, string Tag2, string By, string Instrument, string InstrumentId, string Length, bool Selectable)[] Scores =
    [
        ("s1", "夜風與營火", "", "", "芙蕾雅", "魯特琴", "lute", "3:40", true),
        ("s2", "提爾科納的雨", "", "", "芙蕾雅", "豎琴", "harp", "4:12", true),
        ("s3", "牧羊少年的口哨", "", "", "小麥田", "長笛", "flute", "2:16", true),
        ("s4", "月光下的艾明馬夏", "", "", "北境旅人", "", "", "", true),
        ("s5", "杜巴頓廣場午後", "同名第 1 张", "同名第 1 張", "小麥田", "曼陀林", "mandolin", "3:05", true),
        ("s6", "杜巴頓廣場午後", "同名第 2 张", "同名第 2 張", "", "曼陀林", "mandolin", "3:21", true),
        ("s7", "艾菈的搖籃曲", "", "", "芙蕾雅", "烏克麗麗", "ukulele", "1:58", true),
        ("s8", "遠征隊進行曲", "名称结尾有空白，游戏无法选取", "名稱結尾有空白，遊戲無法選取", "北境旅人", "小鼓", "drum", "2:44", false)
    ];

    /// <summary>页面登记过的“当前显示的乐谱”，按先后记录，供测试核对查询范围。</summary>
    public List<IReadOnlyList<string>> ScoreDetailRequests { get; } = [];

    // 样本的时长是固定值，不需要查询；这里只记下页面登记了哪些乐谱。
    public void RequestScoreDetails(IReadOnlyList<string> scoreIds) => ScoreDetailRequests.Add(scoreIds);

    private readonly List<string> queue = ["s1", "s2", "s3", "s4"];
    private readonly HashSet<string> disabledModules = [];
    private string playlist = "all";
    private string musicSearch = string.Empty;
    private bool musicPlaying = true;

    /// <summary>页面发来的意图，按先后记录，供测试核对。</summary>
    public List<(string Intent, string? Argument)> UiIntents { get; } = [];

    public Task SendIntentAsync(string intent, string? argument, CancellationToken cancellationToken)
    {
        UiIntents.Add((intent, argument));
        switch (intent)
        {
            case "music.search": musicSearch = argument ?? string.Empty; break;
            case "music.playlist" when argument is not null: playlist = argument; break;
            case "music.enqueue" when argument is not null: queue.Add(argument); break;
            case "music.dequeue" when argument is not null && int.TryParse(argument, out var index) && index < queue.Count:
                queue.RemoveAt(index);
                break;
            case "music.start": musicPlaying = true; break;
            case "music.stop": musicPlaying = false; break;
            case "music.next" when queue.Count > 1: queue.Add(queue[0]); queue.RemoveAt(0); break;
            case not null when intent.StartsWith("rooms.", StringComparison.Ordinal): ApplyRoomIntent(intent, argument); break;
            case not null when intent.StartsWith("combat.", StringComparison.Ordinal): ApplyCombatIntent(intent, argument); break;
            case "inventory.account" when argument is not null: inventoryAccount = argument; break;
            // 核心模块同真实后端一样拒绝停用。
            case "modules.toggle" when argument == "game-access": break;
            case "modules.toggle" when argument is not null && !disabledModules.Remove(argument):
                disabledModules.Add(argument);
                break;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public MusicPageData GetMusic()
    {
        var unknown = Pick("未知", "未知");
        var rows = Scores
            .Where(score => playlist switch { "campfire" => score.Id is "s1" or "s2" or "s3", "practice" => score.Id is "s5" or "s7", _ => true })
            .Where(score => musicSearch.Length == 0 || score.Title.Contains(musicSearch, StringComparison.OrdinalIgnoreCase)
                            || score.By.Contains(musicSearch, StringComparison.OrdinalIgnoreCase))
            .Select(score =>
            {
                var playing = musicPlaying && queue.Count > 0 && queue[0] == score.Id;
                return new ScoreRow(score.Id, score.Title, playing ? Pick("演奏中", "演奏中") : Pick(score.Tag, score.Tag2),
                    score.By.Length == 0 ? Pick("未识别", "未辨識") : score.By,
                    score.Instrument.Length == 0 ? Pick("未指定", "未指定") : score.Instrument,
                    score.Length.Length == 0 ? unknown : score.Length, playing, score.Selectable,
                    score.InstrumentId.Length == 0 ? null : score.InstrumentId);
            }).ToList();
        var queued = queue.Select((id, index) =>
        {
            var score = Scores.First(item => item.Id == id);
            var note = score.Instrument.Length == 0 ? Pick("未指定", "未指定") : score.Instrument;
            return new QueueRow(index.ToString(), (index + 1).ToString(), score.Title, note,
                score.Length.Length == 0 ? unknown : score.Length, musicPlaying && index == 0);
        }).ToList();
        return new(Pick("312 张乐谱，今天 14:02 从游戏同步", "312 張樂譜，今天 14:02 從遊戲同步"), musicSearch,
            [
                new("all", Pick("全部乐谱", "全部樂譜"), Pick("312 张", "312 張"), "#161A26", playlist == "all"),
                new("campfire", "營火晚會", Pick("12 首，自定封面", "12 首，自訂封面"), "#E7A23A", playlist == "campfire"),
                new("practice", "練習中", Pick("5 首", "5 首"), "#7C96FF", playlist == "practice")
            ],
            rows,
            Pick($"{queue.Count} 首，已知时长合计 10:08", $"{queue.Count} 首，已知時長合計 10:08"),
            queued,
            [
                new(Pick("播放顺序", "播放順序"), Pick("依序，清单循环", "依序，清單循環")),
                new(Pick("曲间等待", "曲間等待"), Pick("3 秒", "3 秒")),
                new(Pick("检测到合奏时", "偵測到合奏時"), Pick("随对方结束而停止", "隨對方結束而停止"))
            ],
            new(true, true, !musicPlaying && queue.Count > 0, musicPlaying && queue.Count > 1, musicPlaying));
    }

    private string inventoryAccount = "account-1";

    /// <summary>样本背包：两个账号，第一个账号两个角色；艾薇的角色保管箱尚未读取，公共保管箱的“金幣袋”数量未知。</summary>
    public InventoryPageData GetInventory()
    {
        static InventoryCount N(ulong value) => new(value, InventoryCountState.Value);
        var notRead = InventoryCount.NotRead;
        var unknown = new InventoryCount(null, InventoryCountState.Unknown);
        static InventoryCount Partial(ulong value) => new(value, InventoryCountState.Partial);
        var second = inventoryAccount == "account-2";
        IReadOnlyList<InventoryCharacter> characters = second ? [new("c3", "諾娃")] : [new("c1", "琉特"), new("c2", "艾薇")];
        IReadOnlyList<InventoryPivotRow> rows = second
            ?
            [
                new("instrument:7001", "魯特琴", "7001", "ClientCatalog", "instrument", N(1), N(0), [N(1)], [N(0)]),
                new("consumable:5101", "生命藥水 100", "5101", "ClientCatalog", "consumable", N(5), N(0), [N(5)], [N(0)])
            ]
            :
            [
                new("instrument:7001", "魯特琴", "7001", "ClientCatalog", "instrument", Partial(1), N(0), [N(1), N(0)], [N(0), notRead]),
                new("instrument:7002", "豎琴", "7002", "ClientCatalog", "instrument", Partial(1), N(0), [N(1), N(0)], [N(0), notRead]),
                new("instrument:7003", "長笛", "7003", "ClientCatalog", "instrument", Partial(1), N(0), [N(0), N(0)], [N(1), notRead]),
                new("equipment:3301", "冬夜禮服", "3301", "ClientCatalog", "equipment", Partial(1), N(0), [N(1), N(0)], [N(0), notRead]),
                new("consumable:5101", "生命藥水 100", "5101", "ClientCatalog", "consumable", Partial(49), N(0), [N(37), N(12)], [N(0), notRead]),
                new("consumable:5102", "染色安瓶", "5102", "UserConfirmed", "consumable", Partial(12), N(0), [N(12), N(0)], [N(0), notRead]),
                new("material:4021740217", null, "4021740217", null, "material", Partial(240), N(0), [N(0), N(0)], [N(240), notRead]),
                new("misc:9001", "金幣袋", "9001", "ClientCatalog", "misc", Partial(0), unknown, [N(0), N(0)], [N(0), notRead])
            ];
        return new(Pick("已同步 21:14:05，随全局抓包自动更新", "已同步 21:14:05，隨全域抓包自動更新"),
            [new(Pick("道具", "道具"), Pick($"{rows.Count} 种", $"{rows.Count} 種"))],
            [new("account-1", "account-1"), new("account-2", "account-2")], inventoryAccount, characters, rows,
            [
                new(Pick("背包：全部类别", "背包：全部類別"), Pick("完整", "完整"), true),
                new(Pick("角色保管箱：全部类别", "角色保管箱：全部類別"), Pick("完整", "完整"), true),
                new(Pick("公共保管箱：全部类别", "公共保管箱：全部類別"), Pick("完整", "完整"), true)
            ],
            [new(Pick("已收录", "已收錄"), Pick("18,420 条", "18,420 條")), new(Pick("乐器", "樂器"), Pick("126 条", "126 條"))],
            InventoryResourceText.Rows(ResourcesOverride ?? SampleResources()), "c1");
    }

    /// <summary>测试用：替换样本资源视图；为空时用固定样本。</summary>
    internal InventoryResourcesView? ResourcesOverride { get; set; }

    /// <summary>样本资源：金币、银币、魔族貢品与愛心幣已读取（银币、贡品带补充时间），其余未读取。</summary>
    private static InventoryResourcesView SampleResources()
    {
        var now = DateTimeOffset.Now;
        InventoryResourceView Unread(InventoryResourceDefinition d, string? name) => new(d.Key, d.MoneyType, "unread", "inventory.walletUnread",
            name, null, null, null, null, null, null, null, null, null, d.Verified, false, null, d.Scope, d.Scope == InventoryResourceScope.Character ? "c1" : null);
        var names = new Dictionary<string, string> { ["goldFree"] = "金幣", ["goldPaid"] = "金幣", ["silverCoin"] = "銀幣", ["passDeep"] = "魔族貢品", ["heartToken"] = "愛心幣" };
        var items = InventoryResources.Definitions.Select(d => d.Key switch
        {
            "goldFree" => new InventoryResourceView(d.Key, d.MoneyType, "observed", null, "金幣", "在愛爾琳廣泛使用的貨幣", new(988_740), new(102), null,
                now.AddMinutes(-2), null, null, null, null, true, true, "c1", d.Scope, "c1"),
            "goldPaid" => new InventoryResourceView(d.Key, d.MoneyType, "observed", null, "金幣", "在愛爾琳廣泛使用的貨幣", new(0), null, null,
                now.AddMinutes(-9), null, null, null, null, false, true, "c1", d.Scope, "c1"),
            "silverCoin" => new InventoryResourceView(d.Key, d.MoneyType, "observed", null, "銀幣", "進入地下城、狩獵場時支付的入場費", new(63), new(1), null,
                now.AddMinutes(-7), now.AddMinutes(-7), 1800, now.AddMinutes(23), null, true, true, "c1", d.Scope, "c1"),
            "passDeep" => new InventoryResourceView(d.Key, d.MoneyType, "observed", null, "魔族貢品", "進入深層地下城時支付的入場費", new(1), null, null,
                now.AddMinutes(-9), now.AddHours(-7), 43200, now.AddHours(5), null, true, true, "c1", d.Scope, "c1"),
            "heartToken" => new InventoryResourceView(d.Key, d.MoneyType, "observed", null, "愛心幣", null, new(528), new(2), null,
                now.AddMinutes(-3), null, null, null, null, true, true, "c1", d.Scope, "c1"),
            _ => Unread(d, names.GetValueOrDefault(d.Key))
        }).ToArray();
        return new("account-1", "c1", "琉特", true, new(1), items);
    }

    public ModulesPageData GetModules() => new(true,
        new[] { "music-library", "rooms", "color", "combat-observation", "inventory", "presentation" }
            .Select(id => new ModuleState(id, "0.4.6", !disabledModules.Contains(id),
                disabledModules.Contains(id) ? Pick("已停用", "已停用") : Pick("可用", "可用"), string.Empty))
            .Append(new("environment", "0.4.6", true, Pick("不可用", "不可用"), Pick("台服数据来源待接入", "台服資料來源待接入"),
                Pick("环境信息", "環境資訊"), Pick("提供游戏内时间、天气、频道与区域等环境信息", "提供遊戲內時間、天氣、頻道與區域等環境資訊")))
            .Append(new("game-access", "0.4.6", true, Pick("可用", "可用"), string.Empty, Pick("游戏连接", "遊戲連線"),
                Pick("绑定游戏窗口并抓包识别本机角色与地图，为需要游戏数据的模块提供观测", "綁定遊戲視窗並抓包識別本機角色與地圖，為需要遊戲資料的模組提供觀測"),
                Essential: true))
            .ToList(),
        true, false);

    public SettingsPageData GetSettings() => new(
        [
            new(Pick("连接", "連接"), Pick("样本数据，未连接后端", "範例資料，未連接後端")),
            new(Pick("版本", "版本"), "0.4.6"), new("API", "v1")
        ],
        SampleShortcuts(),
        AvailableRelease is { } release
            ?
            [
                new(Pick("当前版本", "目前版本"), "0.4.6"), new(Pick("状态", "狀態"), Pick("有可用更新", "有可用更新")),
                new(Pick("可用更新", "可用更新"), release)
            ]
            :
            [
                new(Pick("当前版本", "目前版本"), "0.4.6"), new(Pick("状态", "狀態"), Pick("已是最新", "已是最新"))
            ],
        !Offline, AvailableRelease is not null, Policy: Offline ? null : samplePolicy,
        AvailableRelease: Offline ? null : AvailableRelease, Connected: !Offline);

    /// <summary>测试用：后端更新状态里发现的新版本标识；为空表示已是最新。</summary>
    internal string? AvailableRelease { get; set; }
}
