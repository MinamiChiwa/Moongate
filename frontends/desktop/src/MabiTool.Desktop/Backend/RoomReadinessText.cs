using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Rooms;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>
/// 合奏相关错误码与房间协议码的短句。后端状态码不直接显示给用户；没有对应短句的码交给共用映射（<see cref="Localization.CodeText"/>）。
/// 房间协议码的文案取原工具 MainWindow.Friendly。
/// </summary>
public static class RoomReadinessText
{
    /// <summary>原因码对应的短句；不需要说明的码返回空，其余交给共用映射。</summary>
    public static string? Reason(string? code) => code switch
    {
        null or "" or "unready" or "user-released" or "readiness-replaced" or "action-finished" => null,
        "input.actionMappingUnavailable" => Pick("未设置开始或停止按键，请在本机动作里设置", "未設定開始或停止按鍵，請在本機動作裡設定"),
        "input.bindingUnavailable" => Pick("尚未绑定游戏窗口", "尚未綁定遊戲視窗"),
        "input.bindingChanged" => Pick("游戏窗口已变化，请重新就绪", "遊戲視窗已變化，請重新就緒"),
        "input.focusChanged" => Pick("无法切换到游戏窗口", "無法切換到遊戲視窗"),
        "input.shortcutConflict" => Pick("开始或停止按键与全局快捷键冲突，请在本机动作里更换", "開始或停止按鍵與全域快捷鍵衝突，請在本機動作裡更換"),
        "input.bindingGeometryMismatch" => Pick("按钮位置与当前游戏窗口尺寸不符，请重新点选按钮位置", "按鈕位置與目前遊戲視窗尺寸不符，請重新點選按鈕位置"),
        "input.stopEvidenceRequired" => Pick("开始和停止需要使用不同的按键或按钮", "開始和停止需要使用不同的按鍵或按鈕"),
        "room.simulationUnavailable" => Pick("模拟模式与后端不一致", "模擬模式與後端不一致"),
        "room.chatDraftUnconfirmed" => Pick("预填指令尚未确认", "預填指令尚未確認"),
        "room.timingOffsetInvalid" => Pick("时间微调超出 −2000～+2000 毫秒", "時間微調超出 −2000～+2000 毫秒"),
        "room.planStale" => Pick("本轮操作已失效，请确认房间状态后重试", "本輪操作已失效，請確認房間狀態後重試"),
        "room.activityActive" => Pick("本轮任务仍在进行，请先取消或统一停止", "本輪任務仍在進行，請先取消或統一停止"),
        "room.clockUnavailable" or "room.clockQualityInsufficient" => Pick("与房间服务的校时尚未完成", "與房間服務的校時尚未完成"),
        "room.memberNotReady" => Pick("选中的成员尚未全部就绪，请让成员就绪或取消勾选", "選中的成員尚未全部就緒，請讓成員就緒或取消勾選"),
        "room.commandUnsupported" => Pick("当前房间服务尚未支持所选停止方式，请更换停止方式或更新房间服务", "目前房間服務尚未支援所選停止方式，請更換停止方式或更新房間服務"),
        "room.interruptionUnavailable" => Pick("当前没有需要处理的演奏中断", "目前沒有需要處理的演奏中斷"),
        "room.commandUnconfirmed" or "resource.conflict" => Pick("房间当前未连接", "房間目前未連線"),
        "room.closeUnconfirmed" => Pick("你已退出，但暂时无法确认其他成员是否都已退出", "你已退出，但暫時無法確認其他成員是否都已退出"),
        "resource.leaseExpired" => Pick("输入占用已过期，请重新就绪", "輸入佔用已過期，請重新就緒"),
        "revision.stale" => Pick("游戏绑定或本机设置已变化，请重试", "遊戲綁定或本機設定已變化，請重試"),
        "permission.denied" => Pick("该操作由房主执行", "該操作由房主執行"),
        "session-cancelled" => Pick("本轮已取消", "本輪已取消"),
        "interrupted" or "cancelled" => Pick("准备被中断", "準備被中斷"),
        "preparation-failed" => Pick("准备没有完成", "準備沒有完成"),
        _ => Localization.CodeText.Reason(code)
    };

    /// <summary>抓包识别的停止、故障或被拒原因（原工具 CaptureChannelWindow.Explain）；不是抓包相关的码返回空。</summary>
    public static string? CaptureReason(string? code) => code switch
    {
        "observation.workerUnavailable" => Pick("宿主未安装或未配置游戏观测组件", "宿主未安裝或未設定遊戲觀測元件"),
        "observation.channelUnavailable" => Pick("所选网卡不可用，请重新列出网卡后选择", "所選網卡不可用，請重新列出網卡後選擇"),
        "observation.npcapMissing" => Pick("未安装 Npcap，抓包需要先安装 Npcap", "未安裝 Npcap，抓包需要先安裝 Npcap"),
        "observation.npcapUnavailable" => Pick("Npcap 已安装但无法加载，请重新安装或更新 Npcap", "Npcap 已安裝但無法載入，請重新安裝或更新 Npcap"),
        "observation.profileUnavailable" => Pick("当前游戏地区或版本尚未支持抓包识别", "目前遊戲地區或版本尚未支援抓包辨識"),
        "observation.captureFailed" => Pick("抓包没有完成，请确认已安装 Npcap，并选择游戏连接所用的网卡", "抓包沒有完成，請確認已安裝 Npcap，並選擇遊戲連線所用的網卡"),
        "observation.readyTimeout" => Pick("抓包启动超时，请确认游戏正在运行，并选择游戏连接所用的网卡", "抓包啟動逾時，請確認遊戲正在執行，並選擇遊戲連線所用的網卡"),
        "observation.sourceEnded" => Pick("抓包已结束，可重新开始识别", "抓包已結束，可重新開始辨識"),
        "input.bindingUnavailable" => Pick("请先选择游戏窗口", "請先選擇遊戲視窗"),
        "input.bindingChanged" => Pick("游戏窗口已变化，请重新开始识别", "遊戲視窗已變化，請重新開始辨識"),
        "resource.busy" => Pick("抓包正在使用其他游戏窗口或网卡，请先停止当前抓包", "抓包正在使用其他遊戲視窗或網卡，請先停止目前抓包"),
        "resource.capacityExceeded" => Pick("抓包识别的使用者已达上限，请先停止其他抓包", "抓包辨識的使用者已達上限，請先停止其他抓包"),
        "permission.denied" => Pick("当前前端没有抓包权限", "目前前端沒有抓包權限"),
        { } other when other.StartsWith("worker.", StringComparison.Ordinal) =>
            Pick($"游戏观测组件异常结束（{other}），可重新开始识别", $"遊戲觀測元件異常結束（{other}），可重新開始辨識"),
        _ => null
    };

    /// <summary>房间协议码（原工具 Friendly）。</summary>
    public static string? Protocol(string? code) => code switch
    {
        "BAD_NICKNAME" => Pick("请填写 1–20 字的昵称。", "請填寫 1–20 字的暱稱。"),
        "MEMBER_NOT_READY" => Pick("选中的成员尚未全部就绪，请让成员就绪或取消勾选。", "選中的成員尚未全部就緒，請讓成員就緒或取消勾選。"),
        "NO_ENSEMBLE_INITIATOR" => Pick("请房主在成员列表中指定至少一位“乐谱使用者”，并勾选其参与本轮。", "請房主在成員列表中指定至少一位「樂譜使用者」，並勾選其參與本輪。"),
        "CANNOT_KICK_HOST" => Pick("退出请点击“关闭房间”，所有成员会一起退出。", "退出請點擊「關閉房間」，所有成員會一起退出。"),
        "BAD_ENSEMBLE_GROUP" => Pick("合奏组编号应为 1–24。", "合奏組編號應為 1–24。"),
        "BAD_ENSEMBLE_ORDER" => Pick("顺序应为 1–24，组内按连续顺序排列。", "順序應為 1–24，組內按連續順序排列。"),
        "BAD_INSTRUMENT" => Pick("乐器名称最多 40 字，请去掉换行和控制字符。", "樂器名稱最多 40 字，請去掉換行和控制字元。"),
        "MEMBER_NOT_FOUND" => Pick("该成员已离开房间，请刷新后重试。", "該成員已離開房間，請重新整理後重試。"),
        "CHARACTER_NOT_OBSERVED" => Pick("暂未识别到这个角色，请等待合奏成员列表更新后重试。", "暫未辨識到這個角色，請等待合奏成員列表更新後重試。"),
        "CAPTURE_IDENTITY_CONFLICT" => Pick("已通过该成员的游戏观测识别其角色，无需手动匹配。", "已透過該成員的遊戲觀測辨識其角色，無需手動匹配。"),
        "FORBIDDEN" => Pick("该操作由房主执行。", "該操作由房主執行。"),
        "SESSION_ACTIVE" => Pick("本轮任务仍在进行，请先取消或统一停止。", "本輪任務仍在進行，請先取消或統一停止。"),
        "USE_STOP_ALL" => Pick("开始时刻已经过去，请使用全员停止。", "開始時刻已經過去，請使用全員停止。"),
        "LEADER_NOT_PARTICIPATING" => Pick("请将指定总指挥加入本轮参与者。", "請將指定總指揮加入本輪參與者。"),
        "STOP_NOT_APPLICABLE" => Pick("表情动作只需同步开始，无需设置停止。", "表情動作只需同步開始，無需設定停止。"),
        "COUNTDOWN_BEFORE_LOCAL_TRIGGER" => Pick("倒计时短于某位成员的提前量，请增加倒计时或减小该成员的提前量。", "倒數短於某位成員的提前量，請增加倒數或減小該成員的提前量。"),
        "ROOM_NOT_FOUND" or "SESSION_EXPIRED" => Pick("房间或身份已失效，请重新创建或加入。", "房間或身分已失效，請重新建立或加入。"),
        "ROOM_FULL" => Pick("房间已满，请房主移出离线成员，或加入其他房间。", "房間已滿，請房主移出離線成員，或加入其他房間。"),
        "RATE_LIMIT" => Pick("操作太快，请稍等几秒再试。", "操作太快，請稍等幾秒再試。"),
        "BAD_PARTICIPANTS" => Pick("本轮成员列表有变化，请重新选择参与者。", "本輪成員列表有變化，請重新選擇參與者。"),
        "BAD_COUNTDOWN" => Pick("倒计时请填写 1–60 秒。", "倒數請填寫 1–60 秒。"),
        "BAD_DURATION" => Pick("演奏时长请填写 0.001–86400 秒。", "演奏時長請填寫 0.001–86400 秒。"),
        "BAD_STOP_RULE" => Pick("无法识别停止方式，请重新选择。", "無法辨識停止方式，請重新選擇。"),
        _ => null
    };

    /// <summary>
    /// 后端拒绝合奏请求时的说明：先看房间协议码，再看错误码，都没有短句时显示“请求未完成：错误码”。
    /// 指定总指挥、移出成员等以成员为目标的操作（<paramref name="intent"/>）遇到 resource.notFound 时说明成员已离开。
    /// </summary>
    public static string Failure(ApiProblem? problem, int status, string? intent = null)
    {
        var protocol = problem?.Details is { } details && details.TryGetValue("protocolCode", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        var code = problem?.Code ?? $"http.{status}";
        if (code == "resource.notFound" && intent is "rooms.leader" or "rooms.kick" or "rooms.roster" or "rooms.move" or "rooms.character")
            protocol ??= "MEMBER_NOT_FOUND";
        if (intent?.StartsWith("rooms.capture-", StringComparison.Ordinal) == true && CaptureReason(code) is { } capture) return capture;
        return Protocol(protocol) ?? Reason(code) ?? Pick("请求未完成", "請求未完成");
    }

    /// <summary>
    /// 轮次命令 Operation 的结局：成功为空；失败、取消或中断给出原因（同 <see cref="Failure"/>）；仍未结束时说明结果待确认。
    /// </summary>
    public static (string Text, RoomTone Tone)? RoundOutcome(OperationSnapshot operation, string? intent = null) => operation.State.KnownValue switch
    {
        OperationState.Succeeded => null,
        OperationState.Failed or OperationState.Cancelled or OperationState.Interrupted =>
            (Failure(operation.Problem, 409, intent), RoomTone.Error),
        _ => (Pick("后端尚未确认本轮命令的结果，请查看房间状态。", "後端尚未確認本輪命令的結果，請查看房間狀態。"), RoomTone.Warning)
    };

    /// <summary>
    /// 本机停止的结果（<c>RoomLocalStopResult.localAction</c>）：已排入停止键、无法准备停止、已取消本机待执行动作，
    /// 或没有可停止的任务。房间已确认时说明由房主决定全员停止或继续。
    /// </summary>
    public static (string Text, RoomTone Tone) LocalStop(string localAction, bool serverConfirmed) => localAction switch
    {
        "stopScheduled" => (serverConfirmed
            ? Pick("已发出本机停止，房主将选择全员停止或继续。", "已送出本機停止，房主將選擇全員停止或繼續。")
            : Pick("已发出本机停止，房间暂未确认。", "已送出本機停止，房間暫未確認。"), serverConfirmed ? RoomTone.Success : RoomTone.Warning),
        "stopRejected" => (Pick("无法执行本机停止，请在游戏中手动停止演奏。", "無法執行本機停止，請在遊戲中手動停止演奏。"), RoomTone.Error),
        "cancelled" => (Pick("已取消本机任务。", "已取消本機任務。"), RoomTone.Success),
        _ => (Pick("本机没有待执行的任务。", "本機沒有待執行的任務。"), RoomTone.Info)
    };
}

/// <summary>合奏请求的组成：就绪、开轮与角色关联。均为纯函数，便于核对请求内容。</summary>
public static class RoomRequests
{
    /// <summary>
    /// 就绪请求。合奏角色取本机成员当前身份，时间微调与修订取本机设置；
    /// 时钟往返、窗口激活耗时与能否开始、停止由后端自己测量并判定，这里保持默认值。
    /// </summary>
    public static RoomReadinessRequest Readiness(bool ready, string musicRole, bool simulation, UInt64String? bindingRevision,
        RoomPreferences preferences) =>
        new(ready, simulation, false, false, preferences.TimingOffsetMs, musicRole, null,
            ExpectedBindingRevision: bindingRevision, ExpectedPreferencesRevision: preferences.Revision);

    /// <summary>
    /// 开轮请求：模式、勾选参与的成员、倒计时，以及合奏的停止方式与它需要的参数。
    /// 表情动作不带任何停止参数；指定时长带 <c>StopAfterMs</c>，总指挥一轮带 <c>TailHoldMs</c>，手动只带停止方式。
    /// </summary>
    public static RoomRoundRequest Round(RoomState room, RoomPreferences preferences)
    {
        var participants = room.Members.Where(member => member.Selected).Select(member => member.Id).ToList();
        if (room.Mode == "emote") return new("emote", participants, preferences.CountdownMs);
        return preferences.MusicStopRule.RawValue switch
        {
            "duration" => new(room.Mode, participants, preferences.CountdownMs, MusicStopRule.Duration, StopAfterMs: preferences.MusicDurationMs),
            "first-cycle" => new(room.Mode, participants, preferences.CountdownMs, MusicStopRule.FirstCycle, TailHoldMs: preferences.MusicTailHoldMs),
            _ => new(room.Mode, participants, preferences.CountdownMs, MusicStopRule.Manual)
        };
    }

    /// <summary>角色关联请求；编号与区服同时为空表示取消匹配。</summary>
    public static RoomCharacterBindingRequest Character(string memberId, string? characterId, string? realmId) =>
        new(memberId, characterId is { Length: > 0 } ? characterId : null, characterId is { Length: > 0 } ? realmId : null);
}
