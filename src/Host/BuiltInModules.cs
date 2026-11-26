using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Host;

public static class BuiltInModules
{
    public static ModuleCatalog CreateCatalog(bool colorRuntime = false)
    {
        ModuleRegistration Register(string id, bool implemented, string[] dependencies) => new(
            new(id, new(0, 1, 0), new(1, 0), new(2, 0),
                dependencies.Select(x => new ModuleDependency(x, "0.1.0")).ToArray(), implemented,
                HighRiskBehaviors: id == "color" ? [ModuleRiskBehavior.MemoryRead] : [],
                DisplayNames: Text(Names[id]), Descriptions: Text(Descriptions[id]), Essential: Essential.Contains(id)),
            _ => implemented ? ValueTask.FromResult<IAsyncDisposable>(new CoreModule())
                : ValueTask.FromException<IAsyncDisposable>(new InvalidOperationException("模块等待实现")));
        return new(new(1, 0), [
            Register("core", true, []),
            Register("game-access", true, ["core"]),
            Register("music-library", true, ["core"]),
            Register("performance", true, ["game-access", "music-library"]),
            Register("rooms", true, ["core"]),
            Register("synchronization", true, ["rooms", "game-access"]),
            Register("color", colorRuntime, ["game-access"]),
            Register("environment", true, ["game-access"]),
            Register("presentation", true, ["core"]),
            Register("combat-observation", true, ["game-access"]),
            Register("inventory", true, ["game-access"]),
            Register("life", true, ["game-access"])
        ]);
    }

    // 工具运行所必需、玩家不能停用的核心模块：宿主核心服务（存储、事件、操作记录与诊断、设置的后端都在其中）与共享抓包的游戏连接。
    private static readonly HashSet<string> Essential = new(StringComparer.Ordinal) { "core", "game-access" };

    private static IReadOnlyDictionary<string, string> Text((string Simplified, string Traditional) text) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["zh-Hans"] = text.Simplified, ["zh-Hant"] = text.Traditional };

    // 模块的界面名称与功能说明，随模块清单（GET /api/v1/modules）返回，前端“模块与扩展”页据此显示。
    private static readonly Dictionary<string, (string, string)> Names = new(StringComparer.Ordinal)
    {
        ["core"] = ("核心服务", "核心服務"),
        ["game-access"] = ("游戏连接", "遊戲連線"),
        ["music-library"] = ("乐谱库", "樂譜庫"),
        ["performance"] = ("游戏内演奏", "遊戲內演奏"),
        ["rooms"] = ("合奏房间", "合奏房間"),
        ["synchronization"] = ("合奏同步", "合奏同步"),
        ["color"] = ("染色", "染色"),
        ["environment"] = ("环境信息", "環境資訊"),
        ["presentation"] = ("浮窗展示数据", "浮窗展示資料"),
        ["combat-observation"] = ("DPS检测", "DPS檢測"),
        ["inventory"] = ("背包", "背包"),
        ["life"] = ("生活", "生活")
    };

    private static readonly Dictionary<string, (string, string)> Descriptions = new(StringComparer.Ordinal)
    {
        ["core"] = ("宿主的基础服务（存储、事件与操作记录），其他模块都依赖它", "宿主的基礎服務（儲存、事件與操作記錄），其他模組都依賴它"),
        ["game-access"] = ("绑定游戏窗口并抓包识别本机角色与地图，为需要游戏数据的模块提供观测", "綁定遊戲視窗並抓包識別本機角色與地圖，為需要遊戲資料的模組提供觀測"),
        ["music-library"] = ("管理本机乐谱、清单与演奏队列", "管理本機樂譜、清單與演奏佇列"),
        ["performance"] = ("按演奏队列在游戏里选曲并演奏", "依演奏佇列在遊戲裡選曲並演奏"),
        ["rooms"] = ("建立与加入合奏房间，管理成员、分组与每一轮的流程", "建立與加入合奏房間，管理成員、分組與每一輪的流程"),
        ["synchronization"] = ("按房间的时间基准在本机同步开始与停止演奏或表情动作", "依房間的時間基準在本機同步開始與停止演奏或表情動作"),
        ["color"] = ("按目标色在游戏里寻找染色方案并复核实测颜色", "依目標色在遊戲裡尋找染色方案並複核實測顏色"),
        ["environment"] = ("提供游戏内时间、天气、频道与区域等环境信息", "提供遊戲內時間、天氣、頻道與區域等環境資訊"),
        ["presentation"] = ("为游戏浮窗元件提供附近演奏、环境等展示数据", "為遊戲浮窗元件提供附近演奏、環境等展示資料"),
        ["combat-observation"] = ("被动观测本机战斗，统计伤害、DPS、技能与目标、效果和定时测量", "被動觀測本機戰鬥，統計傷害、DPS、技能與目標、效果和定時測量"),
        ["inventory"] = ("读取各角色的背包与保管箱道具，汇总成透视表", "讀取各角色的背包與保管箱道具，彙總成樞紐表"),
        ["life"] = ("经游戏连接器采集、加工、制作，并提供原地专注钓鱼", "經遊戲連接器採集、加工、製作，並提供原地專注釣魚")
    };

    private sealed class CoreModule : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
