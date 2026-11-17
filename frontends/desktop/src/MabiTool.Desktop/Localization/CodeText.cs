using System.Collections.Concurrent;
using System.Globalization;

namespace MabiTool.Desktop.Localization;

/// <summary>
/// 后端与前端内部标识（操作类型、操作阶段、状态、原因码、模块与资源标识）换成界面文字的唯一入口，简繁各一份。
/// 页面、外壳、浮窗与各模块的文案表都经这里收尾：有文案的标识只显示中文；没有文案的显示中文概括说明并括注原文，
/// 同时记入 <see cref="Unmapped"/>（诊断页列出），便于补齐文案。操作号这类对用户无意义的内容不经这里，也不在主界面显示。
/// </summary>
public static class CodeText
{
    private static readonly Dictionary<string, (string Hans, string Hant)> Operations = new(StringComparer.Ordinal)
    {
        ["color.search"] = ("染色寻色", "染色尋色"),
        ["color.settings.import"] = ("导入染色设置", "匯入染色設定"),
        ["combat.observe"] = ("DPS 观测", "DPS 觀測"),
        ["inventory.observe"] = ("背包观测", "背包觀測"),
        ["inventory.import"] = ("导入背包数据", "匯入背包資料"),
        ["inventory.import.preview"] = ("预览背包导入", "預覽背包匯入"),
        ["inventory.export"] = ("导出背包数据", "匯出背包資料"),
        ["inventory.catalog.import"] = ("导入道具目录", "匯入道具目錄"),
        ["inventory.catalog.refresh"] = ("更新道具目录", "更新道具目錄"),
        ["music.import"] = ("导入乐谱", "匯入樂譜"),
        ["music.import.preview"] = ("预览乐谱导入", "預覽樂譜匯入"),
        ["music.import.commit"] = ("保存导入的乐谱", "儲存匯入的樂譜"),
        ["music.edit"] = ("编辑乐谱", "編輯樂譜"),
        ["ensemble.start"] = ("开始合奏", "開始合奏"),
        ["ensemble.stop"] = ("停止合奏", "停止合奏"),
        ["room.server.start"] = ("启动房间服务", "啟動房間服務"),
        ["room.server.stop"] = ("停止房间服务", "停止房間服務"),
        ["room.round"] = ("合奏本轮", "合奏本輪"),
        ["room.round.prepare"] = ("准备本轮合奏", "準備本輪合奏"),
        ["room.round.control"] = ("控制本轮合奏", "控制本輪合奏"),
        ["room.settings.import"] = ("导入合奏设置", "匯入合奏設定"),
        ["diagnostics.export"] = ("导出排查包", "匯出排查包"),
        ["files.export"] = ("导出文件", "匯出檔案"),
        ["file.export"] = ("导出文件", "匯出檔案"),
        ["replay.run"] = ("离线回放", "離線回放"),
        ["storage.backup"] = ("备份数据", "備份資料"),
        ["storage.restore"] = ("恢复数据", "還原資料"),
        ["storage.migrate"] = ("迁移数据", "遷移資料"),
        ["storage.moduleDelete"] = ("删除模块数据", "刪除模組資料"),
        ["update.check"] = ("检查更新", "檢查更新"),
        ["update.download"] = ("下载更新", "下載更新"),
        ["update.apply"] = ("安装更新", "安裝更新"),
        ["module.configure"] = ("修改模块配置", "修改模組設定"),
        ["game.activate"] = ("切换到游戏窗口", "切換到遊戲視窗")
    };

    private static readonly Dictionary<string, (string Hans, string Hant)> Phases = new(StringComparer.Ordinal)
    {
        ["queued"] = ("排队中", "排隊中"),
        ["idle"] = ("空闲", "閒置"),
        ["armed"] = ("已就绪，等待开始", "已就緒，等待開始"),
        ["executing"] = ("执行中", "執行中"),
        ["performing"] = ("演奏中", "演奏中"),
        ["waiting"] = ("等待中", "等待中"),
        ["preparing"] = ("准备中", "準備中"),
        ["prepared"] = ("已准备", "已準備"),
        ["starting"] = ("正在启动", "正在啟動"),
        ["running"] = ("进行中", "進行中"),
        ["observing"] = ("观测中", "觀測中"),
        ["identity-pending"] = ("等待识别本机角色", "等待識別本機角色"),
        ["waiting-user"] = ("等待你的操作", "等待你的操作"),
        ["validating"] = ("正在检查", "正在檢查"),
        ["exporting"] = ("正在导出", "正在匯出"),
        ["packing"] = ("正在打包", "正在打包"),
        ["staged"] = ("已暂存", "已暫存"),
        ["applying"] = ("正在应用", "正在套用"),
        ["copyPrepared"] = ("已准备复制", "已準備複製"),
        ["copied"] = ("已复制", "已複製"),
        ["filesPrepared"] = ("文件已准备", "檔案已準備"),
        ["filesRestored"] = ("文件已恢复", "檔案已還原"),
        ["databaseRestored"] = ("数据库已恢复", "資料庫已還原"),
        ["pointerSwitched"] = ("已切换到新版本", "已切換到新版本"),
        ["handedOff"] = ("已交给安装程序", "已交給安裝程式"),
        ["backend-restarted"] = ("后端已重启", "後端已重啟"),
        ["healthChecking"] = ("正在检查运行状况", "正在檢查執行狀況"),
        ["healthy"] = ("运行正常", "執行正常"),
        ["originalRetained"] = ("已保留原版本", "已保留原版本"),
        ["rollbackStarted"] = ("正在回退", "正在回退"),
        ["rolledBack"] = ("已回退", "已回退"),
        ["committed"] = ("已保存", "已儲存"),
        ["stopping"] = ("正在停止", "正在停止"),
        ["stopped"] = ("已停止", "已停止"),
        ["succeeded"] = ("已完成", "已完成"),
        ["failed"] = ("失败", "失敗"),
        ["cancelled"] = ("已取消", "已取消"),
        ["interrupted"] = ("已中断", "已中斷")
    };

    private static readonly Dictionary<string, (string Hans, string Hant)> States = new(StringComparer.Ordinal)
    {
        ["queued"] = ("排队中", "排隊中"),
        ["pending"] = ("等待中", "等待中"),
        ["planned"] = ("已计划", "已計劃"),
        ["prepared"] = ("已准备", "已準備"),
        ["unprepared"] = ("未准备", "未準備"),
        ["ready"] = ("就绪", "就緒"),
        ["starting"] = ("正在启动", "正在啟動"),
        ["running"] = ("运行中", "執行中"),
        ["executing"] = ("执行中", "執行中"),
        ["active"] = ("进行中", "進行中"),
        ["uploading"] = ("上传中", "上傳中"),
        ["deleting"] = ("删除中", "刪除中"),
        ["observing"] = ("观测中", "觀測中"),
        ["waiting"] = ("等待中", "等待中"),
        ["waitingUser"] = ("等待你的操作", "等待你的操作"),
        ["waiting-user"] = ("等待你的操作", "等待你的操作"),
        ["cancelRequested"] = ("正在取消", "正在取消"),
        ["stopping"] = ("正在停止", "正在停止"),
        ["stopped"] = ("已停止", "已停止"),
        ["off"] = ("已关闭", "已關閉"),
        ["idle"] = ("待命，使用时才启动", "待命，使用時才啟動"),
        ["busy"] = ("忙碌", "忙碌"),
        ["warm"] = ("已就绪，待命中", "已就緒，待命中"),
        ["succeeded"] = ("已完成", "已完成"),
        ["complete"] = ("已完成", "已完成"),
        ["completed"] = ("已完成", "已完成"),
        ["committed"] = ("已保存", "已儲存"),
        ["mapped"] = ("已对应", "已對應"),
        ["failed"] = ("失败", "失敗"),
        ["faulted"] = ("故障", "故障"),
        ["cancelled"] = ("已取消", "已取消"),
        ["interrupted"] = ("已中断", "已中斷"),
        ["available"] = ("可用", "可用"),
        ["unavailable"] = ("不可用", "不可用"),
        ["enabled"] = ("已启用", "已啟用"),
        ["disabled"] = ("已停用", "已停用"),
        ["unconfigured"] = ("未配置", "未設定"),
        ["degraded"] = ("部分可用", "部分可用"),
        ["bound"] = ("已绑定", "已綁定"),
        ["unbound"] = ("未绑定", "未綁定"),
        ["lost"] = ("窗口已关闭或无法访问", "視窗已關閉或無法存取"),
        ["stale"] = ("绑定已过期，请重新选择", "綁定已過期，請重新選擇"),
        ["selecting"] = ("正在选择窗口", "正在選擇視窗"),
        ["registered"] = ("已注册", "已註冊"),
        ["up-to-date"] = ("已是最新", "已是最新"),
        ["connected"] = ("已连线", "已連線"),
        ["disconnected"] = ("未连线", "未連線"),
        ["confirmed"] = ("观测完整", "觀測完整"),
        ["partial"] = ("部分观测", "部分觀測"),
        ["exact-hex"] = ("精确色码", "精確色碼"),
        ["similar"] = ("相似即可", "相似即可"),
        ["screenshot"] = ("截图读取", "截圖讀取"),
        ["unknown"] = ("未知", "未知")
    };

    private static readonly Dictionary<string, (string Hans, string Hant)> Reasons = new(StringComparer.Ordinal)
    {
        ["permission.denied"] = ("没有权限", "沒有權限"),
        ["provider.unavailable"] = ("数据来源不可用", "資料來源不可用"),
        ["provider.unconfigured"] = ("数据来源未配置", "資料來源未設定"),
        ["game.validationRequired"] = ("游戏版本尚未核验", "遊戲版本尚未核驗"),
        ["gameBuild.incompatible"] = ("游戏版本尚未核验", "遊戲版本尚未核驗"),
        ["capability.unavailable"] = ("这项功能当前不可用", "這項功能目前不可用"),
        ["module.notImplemented"] = ("后端尚未实现，暂不可用", "後端尚未實作，暫不可用"),
        ["module.inUse"] = ("模块正在使用中", "模組正在使用中"),
        ["module.essential"] = ("核心模块不能停用", "核心模組不能停用"),
        ["module.configuration"] = ("模块配置无效", "模組設定無效"),
        ["system.unexpectedError"] = ("后端内部出错", "後端內部出錯"),
        ["frontend.notConnected"] = ("未连接后端", "未連接後端"),
        ["request.invalid"] = ("请求内容无效", "請求內容無效"),
        ["request.bodyTooLarge"] = ("请求内容过大", "請求內容過大"),
        ["resource.busy"] = ("游戏输入正被其他操作占用", "遊戲輸入正被其他操作佔用"),
        ["resource.notFound"] = ("找不到所选内容", "找不到所選內容"),
        ["resource.conflict"] = ("内容已被其他操作修改", "內容已被其他操作修改"),
        ["resource.capacityExceeded"] = ("数量已达上限", "數量已達上限"),
        ["resource.leaseExpired"] = ("输入占用已过期", "輸入佔用已過期"),
        ["revision.stale"] = ("数据已变化，请刷新后重试", "資料已變化，請重新整理後重試"),
        ["revision.required"] = ("缺少版本信息，请刷新后重试", "缺少版本資訊，請重新整理後重試"),
        ["idempotency.conflict"] = ("同一请求的内容不一致", "同一請求的內容不一致"),
        ["idempotency.required"] = ("请求缺少去重标记", "請求缺少去重標記"),
        ["operation.executionFailed"] = ("执行失败", "執行失敗"),
        ["operation.interrupted"] = ("操作被中断", "操作被中斷"),
        ["operation.notCancellable"] = ("这项操作不能取消", "這項操作不能取消"),
        ["worker.busy"] = ("计算进程忙碌，请稍后再试", "計算程序忙碌，請稍後再試"),
        ["worker.resultInvalid"] = ("计算结果无效", "計算結果無效"),
        ["game.bindingRequired"] = ("尚未绑定游戏窗口", "尚未綁定遊戲視窗"),
        ["game.selectionExpired"] = ("窗口选择已过期，请重新选择", "視窗選擇已過期，請重新選擇"),
        ["input.bindingUnavailable"] = ("尚未绑定游戏窗口", "尚未綁定遊戲視窗"),
        ["input.bindingChanged"] = ("游戏窗口绑定已变化", "遊戲視窗綁定已變化"),
        ["input.focusChanged"] = ("无法切换到游戏窗口", "無法切換到遊戲視窗"),
        ["input.controlOccupied"] = ("游戏输入正被其他模块占用", "遊戲輸入正被其他模組佔用"),
        ["input.deviceFailure"] = ("发送输入失败", "發送輸入失敗"),
        ["input.leaseInvalid"] = ("输入占用已失效", "輸入佔用已失效"),
        ["input.releaseFailed"] = ("没能释放游戏输入", "沒能釋放遊戲輸入"),
        ["input.runStopped"] = ("输入已被停止", "輸入已被停止"),
        ["observation.captureStopped"] = ("抓包已停止", "抓包已停止"),
        ["observation.unavailable"] = ("游戏观测不可用", "遊戲觀測不可用"),
        ["observation.readyTimeout"] = ("观测启动超时", "觀測啟動逾時"),
        ["combat.sourceUnavailable"] = ("战斗来源不可用", "戰鬥來源不可用"),
        ["combat.identityUnresolved"] = ("没能识别本机角色", "沒能識別本機角色"),
        ["combat.buildUnsupported"] = ("当前客户端版本不受支持", "目前用戶端版本不受支援"),
        ["combat.autoDisabled"] = ("自动观测已关闭", "自動觀測已關閉"),
        ["inventory.sourceUnavailable"] = ("背包来源不可用", "背包來源不可用"),
        ["inventory.buildUnsupported"] = ("当前客户端版本不受支持", "目前用戶端版本不受支援"),
        ["room.serverUnavailable"] = ("房间服务不可用", "房間服務不可用"),
        ["storage.unavailable"] = ("存储不可用", "儲存不可用"),
        ["storage.quotaExceeded"] = ("存储空间已达上限", "儲存空間已達上限"),
        ["update.maintenanceActive"] = ("正在安装更新", "正在安裝更新"),
        ["update.sourceNotConfigured"] = ("没有配置更新来源", "沒有設定更新來源"),
        ["update.installationFailed"] = ("更新安装失败", "更新安裝失敗"),
        ["hostStopping"] = ("宿主退出", "宿主退出"),
        ["restarted"] = ("宿主重启", "宿主重啟"),
        ["expired"] = ("到期", "到期"),
        ["unknown"] = ("原因未知", "原因未知")
    };

    private static readonly Dictionary<string, (string Hans, string Hant)> Modules = new(StringComparer.Ordinal)
    {
        ["core"] = ("核心", "核心"),
        ["game-access"] = ("游戏连接", "遊戲連線"),
        ["combat-observation"] = ("DPS检测", "DPS檢測"),
        ["combat"] = ("DPS检测", "DPS檢測"),
        ["inventory"] = ("背包", "背包"),
        ["music-library"] = ("乐谱库", "樂譜庫"),
        ["music"] = ("演奏", "演奏"),
        ["rooms"] = ("合奏", "合奏"),
        ["ensemble"] = ("合奏", "合奏"),
        ["color"] = ("染色", "染色"),
        ["presentation"] = ("浮窗版面", "浮窗版面"),
        ["environment"] = ("环境信息", "環境資訊"),
        ["diagnostics"] = ("诊断", "診斷"),
        ["storage"] = ("数据存储", "資料儲存"),
        ["update"] = ("更新", "更新")
    };

    private static readonly Dictionary<string, (string Hans, string Hant)> Resources = new(StringComparer.Ordinal)
    {
        ["game.input"] = ("游戏输入", "遊戲輸入"),
        ["game.control"] = ("游戏控制", "遊戲控制"),
        ["game.capture"] = ("游戏画面读取", "遊戲畫面讀取"),
        ["game.observation"] = ("游戏观测", "遊戲觀測"),
        ["game.binding"] = ("游戏窗口绑定", "遊戲視窗綁定")
    };

    private static readonly ConcurrentDictionary<string, byte> unmapped = new(StringComparer.Ordinal);

    /// <summary>界面上出现过、还没有文案的标识（“类别:原文”），按出现先后不保证顺序；诊断页列出。</summary>
    public static IReadOnlyCollection<string> Unmapped => unmapped.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>出现新的未登记标识。</summary>
    public static event EventHandler? UnmappedChanged;

    public static IReadOnlyCollection<string> KnownOperations => Operations.Keys;
    public static IReadOnlyCollection<string> KnownPhases => Phases.Keys;
    public static IReadOnlyCollection<string> KnownStates => States.Keys;
    public static IReadOnlyCollection<string> KnownReasons => Reasons.Keys;
    public static IReadOnlyCollection<string> KnownModules => Modules.Keys;

    /// <summary>操作类型，例如 color.search →“染色寻色”。</summary>
    public static string Operation(string? kind) =>
        Lookup(Operations, kind) ?? Fallback("operation", kind, "未登记的操作", "未登記的操作", "未知操作", "未知操作");

    /// <summary>操作阶段；<paramref name="domain"/> 给出模块自己的阶段说明（例如染色寻色的阶段表），先于通用阶段。</summary>
    public static string Phase(string? phase, Func<string, string?>? domain = null) =>
        (phase is { Length: > 0 } && domain?.Invoke(phase) is { Length: > 0 } own ? own : null)
        ?? Lookup(Phases, phase) ?? Fallback("phase", phase, "处理中", "處理中", "处理中", "處理中");

    /// <summary>运行、观测、绑定、模块等状态。</summary>
    public static string State(string? state) =>
        Lookup(States, state) ?? Fallback("state", state, "状态未登记", "狀態未登記", "状态未知", "狀態未知");

    /// <summary>原因码与错误码（含 http.状态码）；只给原因本身的短句，调用方按语境加前缀。</summary>
    public static string Reason(string? code) =>
        TryReason(code, out var text) ? text : Fallback("reason", code, "后端返回了未登记的原因", "後端回傳了未登記的原因", "原因未知", "原因未知");

    /// <summary>有文案的原因码给出短句；http.状态码按状态类别给出。</summary>
    public static bool TryReason(string? code, out string text)
    {
        if (Lookup(Reasons, code) is { } known) { text = known; return true; }
        if (code is { Length: > 5 } && code.StartsWith("http.", StringComparison.Ordinal)
            && int.TryParse(code.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            text = Http(status);
            return true;
        }
        text = string.Empty;
        return false;
    }

    /// <summary>模块标识；有前端页面的模块优先用模块目录里的标题，这里给没有页面或目录未加载时的名称。</summary>
    public static string Module(string? id) =>
        Lookup(Modules, id) ?? Fallback("module", id, "未登记的模块", "未登記的模組", "未知模块", "未知模組");

    /// <summary>被占用的资源标识，例如 game.input →“游戏输入”。</summary>
    public static string Resource(string? id) =>
        Lookup(Resources, id) ?? Fallback("resource", id, "未登记的资源", "未登記的資源", "未知资源", "未知資源");

    private static string Http(int status) => status switch
    {
        400 => UiText.Pick("请求内容无效（HTTP 400）", "請求內容無效（HTTP 400）"),
        401 => UiText.Pick("没有通过身份验证（HTTP 401）", "沒有通過身分驗證（HTTP 401）"),
        403 => UiText.Pick("没有权限（HTTP 403）", "沒有權限（HTTP 403）"),
        404 => UiText.Pick("找不到所请求的内容（HTTP 404）", "找不到所請求的內容（HTTP 404）"),
        408 or 504 => UiText.Pick($"后端处理超时（HTTP {status}）", $"後端處理逾時（HTTP {status}）"),
        409 => UiText.Pick("内容已被其他操作修改（HTTP 409）", "內容已被其他操作修改（HTTP 409）"),
        429 => UiText.Pick("请求过于频繁（HTTP 429）", "請求過於頻繁（HTTP 429）"),
        503 => UiText.Pick("后端暂时不可用（HTTP 503）", "後端暫時不可用（HTTP 503）"),
        >= 500 => UiText.Pick($"后端内部出错（HTTP {status}）", $"後端內部出錯（HTTP {status}）"),
        _ => UiText.Pick($"后端拒绝了请求（HTTP {status}）", $"後端拒絕了請求（HTTP {status}）")
    };

    private static string? Lookup(Dictionary<string, (string Hans, string Hant)> table, string? key) =>
        key is not null && table.TryGetValue(key, out var text) ? UiText.Pick(text.Hans, text.Hant) : null;

    // 没有文案：中文概括并括注原文，记一条待补的标识；没有原文时只给概括。
    private static string Fallback(string category, string? raw, string hans, string hant, string emptyHans, string emptyHant)
    {
        if (raw is not { Length: > 0 }) return UiText.Pick(emptyHans, emptyHant);
        if (unmapped.TryAdd(category + ":" + raw, 0)) UnmappedChanged?.Invoke(null, EventArgs.Empty);
        return UiText.Pick($"{hans}（{raw}）", $"{hant}（{raw}）");
    }
}
