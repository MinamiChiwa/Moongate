using MabiTool.Contracts;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 权限目录的一项。<see cref="Methods"/> 是这项权限放行的协议方法，<see cref="Topics"/> 是可订阅的事件主题；
/// <see cref="High"/> 的权限安装后默认关闭，由用户逐项打开。
/// </summary>
public sealed record ExtensionPermission(string Id, bool High, string NameHans, string NameHant, string DescriptionHans, string DescriptionHant,
    IReadOnlyList<string> Methods, IReadOnlyList<string> Topics)
{
    public string Risk => High ? "high" : "normal";
    public IReadOnlyDictionary<string, string> Names => new Dictionary<string, string> { ["zh-Hans"] = NameHans, ["zh-Hant"] = NameHant };
    public IReadOnlyDictionary<string, string> Descriptions => new Dictionary<string, string> { ["zh-Hans"] = DescriptionHans, ["zh-Hant"] = DescriptionHant };
}

/// <summary>
/// 扩展权限目录（协议版本 1）。扩展只能调用清单声明且已被授予的权限对应的方法；新增能力在这里登记一项权限，
/// 不改变已有权限的含义。读取游戏进程内存不向扩展开放：清单声明 <c>process.read.selected-game</c> 时以
/// <c>extension.permissionUnavailable</c> 拒绝安装。
/// </summary>
public static class ExtensionPermissions
{
    public const string ProcessRead = "process.read.selected-game";

    public static IReadOnlyList<ExtensionPermission> All { get; } = Array.AsReadOnly<ExtensionPermission>([
        new("combat.read", false, "读取战斗数据", "讀取戰鬥資料", "DPS、效果与分段的快照，以及战斗变化事件", "DPS、效果與分段的快照，以及戰鬥變化事件",
            ["combat.snapshot"], ["combat.summary.changed", "combat.effects.changed", "combat.encounter.closed", "combat.observation.degraded", "combat.measurement.changed"]),
        new("inventory.read", false, "读取背包与保管箱", "讀取背包與保管箱", "库存账户与物品查询，以及库存变化事件", "庫存帳戶與物品查詢，以及庫存變化事件",
            ["inventory.accounts", "inventory.items"], ["inventory.snapshot.changed", "inventory.observation.degraded"]),
        new("game.read", false, "读取游戏绑定与地图频道", "讀取遊戲綁定與地圖頻道", "当前绑定的游戏窗口、抓包状态、地图与频道", "目前綁定的遊戲視窗、抓包狀態、地圖與頻道",
            ["game.binding", "game.observation"], ["game.observation.changed", "game.environment.changed"]),
        new("rooms.read", false, "读取合奏房间", "讀取合奏房間", "当前合奏房间的成员与轮次状态", "目前合奏房間的成員與輪次狀態",
            ["rooms.state"], []),
        new("music.read", false, "查询曲库", "查詢曲庫", "按条件查询本机曲库", "按條件查詢本機曲庫",
            ["music.query"], []),
        new("ui.page", false, "提供页面", "提供頁面", "在侧栏“扩展”分组里显示扩展的页面", "在側欄「擴充」分組裡顯示擴充的頁面",
            ["ui.page.set", "ui.page.remove", "ui.state.set"], []),
        new("ui.overlay", false, "提供浮窗元件", "提供浮窗元件", "在浮窗元件库里提供扩展的元件", "在浮窗元件庫裡提供擴充的元件",
            ["ui.overlay.set", "ui.overlay.remove", "ui.state.set"], []),
        new("ui.settings", false, "提供设置项", "提供設定項", "在设置页“扩展”区显示清单声明的设置项，并读取其值", "在設定頁「擴充」區顯示清單宣告的設定項，並讀取其值",
            ["settings.get"], []),
        new("ui.activity", false, "显示活动栏状态", "顯示活動欄狀態", "在活动栏显示一句状态", "在活動欄顯示一句狀態",
            ["ui.activity.set"], []),
        new("ui.shortcut", false, "快捷键命令", "快捷鍵命令", "把清单声明的命令登记到快捷键设置，按下时通知扩展", "把清單宣告的命令登記到快捷鍵設定，按下時通知擴充",
            [], []),
        new("storage.local", false, "扩展自己的存储", "擴充自己的儲存", "在扩展自己的数据目录里读写键值与文件，总量 16 MiB", "在擴充自己的資料目錄裡讀寫鍵值與檔案，總量 16 MiB",
            ["storage.get", "storage.set", "storage.delete", "storage.list", "storage.file.read", "storage.file.write", "storage.file.delete"], []),
        new("input.selected-window", true, "向游戏发送输入", "向遊戲傳送輸入", "以前台或后台方式操作游戏（点击、拖动、长按、滚动、按键），经输入仲裁、租约与紧急停止约束；这会自动操作游戏",
            "以前景或背景方式操作遊戲（點擊、拖曳、長按、捲動、按鍵），經輸入仲裁、租約與緊急停止約束；這會自動操作遊戲",
            ["input.acquire", "input.send", "input.release"], []),
        new("capture.selected-window", true, "截取游戏画面", "擷取遊戲畫面", "截取当前绑定的游戏窗口画面", "擷取目前綁定的遊戲視窗畫面",
            ["capture.game"], []),
        new("network.fetch", true, "访问网络", "存取網路", "经宿主访问清单声明的主机（HTTPS）", "經宿主存取清單宣告的主機（HTTPS）",
            ["network.fetch"], []),
        new("files.read-selected", true, "读取本机文件", "讀取本機檔案", "读取用户每次在文件选择框里选定的文件", "讀取使用者每次在檔案選擇框裡選定的檔案",
            ["files.pick"], [])]);

    private static readonly Dictionary<string, ExtensionPermission> ById = All.ToDictionary(x => x.Id, StringComparer.Ordinal);

    /// <summary>清单可以声明的全部名称：目录里的权限与已登记但不开放的进程读取。</summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(All.Select(x => x.Id).Append(ProcessRead), StringComparer.Ordinal);

    public static ExtensionPermission? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>不需要权限的协议方法：握手、心跳、日志、事件订阅与取消（订阅只接受已授予权限的主题）。</summary>
    public static IReadOnlySet<string> Unrestricted { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "log", "events.subscribe", "events.unsubscribe", "$/cancel" };

    public static ExtensionPermission? ForMethod(string method) => All.FirstOrDefault(x => x.Methods.Contains(method, StringComparer.Ordinal));
    public static ExtensionPermission? ForTopic(string topic) => All.FirstOrDefault(x => x.Topics.Contains(topic, StringComparer.Ordinal));

    public static ExtensionPermissionInfo Info(string id, bool added = false)
    {
        var item = Find(id)!;
        return new(item.Id, item.Risk, item.Names, item.Descriptions, added);
    }

    public static ExtensionPermissionState State(string id, bool granted)
    {
        var item = Find(id)!;
        return new(item.Id, item.Risk, item.Names, item.Descriptions, granted);
    }
}
