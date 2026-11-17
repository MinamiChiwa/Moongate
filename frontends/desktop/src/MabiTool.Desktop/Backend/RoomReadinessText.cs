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
        "input.actionMappingUnavailable" => Pick("未设置开始或停止按键", "未設定開始或停止按鍵"),
        "input.bindingUnavailable" => Pick("尚未绑定游戏窗口", "尚未綁定遊戲視窗"),
        "input.bindingChanged" => Pick("游戏窗口已变化，请重新就绪", "遊戲視窗已變化，請重新就緒"),
        "input.focusChanged" => Pick("无法切换到游戏窗口", "無法切換到遊戲視窗"),
        "input.shortcutConflict" => Pick("按键与全局快捷键冲突，请更换", "按鍵與全域快捷鍵衝突，請更換"),
        "input.bindingGeometryMismatch" => Pick("按钮位置与窗口不符，请重新点选", "按鈕位置與視窗不符，請重新點選"),
        "input.stopEvidenceRequired" => Pick("开始与停止需用不同按键", "開始與停止需用不同按鍵"),
        "room.simulationUnavailable" => Pick("模拟模式与后端不一致", "模擬模式與後端不一致"),
        "room.chatDraftUnconfirmed" => Pick("预填指令尚未确认", "預填指令尚未確認"),
        "room.timingOffsetInvalid" => Pick("时间微调超出 ±2000 毫秒", "時間微調超出 ±2000 毫秒"),
        "room.planStale" => Pick("本轮操作已失效，请重试", "本輪操作已失效，請重試"),
        "room.activityActive" => Pick("本轮仍在进行，请先取消或全停", "本輪仍在進行，請先取消或全停"),
        "room.clockUnavailable" or "room.clockQualityInsufficient" => Pick("与房间的校时未完成", "與房間的校時未完成"),
        "room.memberNotReady" => Pick("有选中的成员未就绪", "有選中的成員未就緒"),
        "room.commandUnsupported" => Pick("房间服务不支持该停止方式", "房間服務不支援該停止方式"),
        "room.interruptionUnavailable" => Pick("没有需要处理的中断", "沒有需要處理的中斷"),
        "room.commandUnconfirmed" or "resource.conflict" => Pick("房间当前未连接", "房間目前未連線"),
        "room.closeUnconfirmed" => Pick("你已退出，未确认全员退出", "你已退出，未確認全員退出"),
        "resource.leaseExpired" => Pick("输入占用已过期，请重新就绪", "輸入佔用已過期，請重新就緒"),
        "revision.stale" => Pick("绑定或设置已变化，请重试", "綁定或設定已變化，請重試"),
        "permission.denied" => Pick("该操作由房主执行", "該操作由房主執行"),
        "session-cancelled" => Pick("本轮已取消", "本輪已取消"),
        "interrupted" or "cancelled" => Pick("准备被中断", "準備被中斷"),
        "preparation-failed" => Pick("准备没有完成", "準備沒有完成"),
        _ => Localization.CodeText.Reason(code)
    };

    /// <summary>抓包识别的停止、故障或被拒原因（原工具 CaptureChannelWindow.Explain）；不是抓包相关的码返回空。</summary>
    public static string? CaptureReason(string? code) => code switch
    {
        "observation.workerUnavailable" => Pick("未安装游戏观测组件", "未安裝遊戲觀測元件"),
        "observation.channelUnavailable" => Pick("所选网卡不可用，请重新选择", "所選網卡不可用，請重新選擇"),
        "observation.npcapMissing" => Pick("未安装 Npcap", "未安裝 Npcap"),
        "observation.npcapUnavailable" => Pick("Npcap 无法加载，请重装", "Npcap 無法載入，請重裝"),
        "observation.profileUnavailable" => Pick("该游戏版本不支持抓包", "該遊戲版本不支援抓包"),
        "observation.captureFailed" => Pick("抓包失败，请检查 Npcap 与网卡", "抓包失敗，請檢查 Npcap 與網卡"),
        "observation.readyTimeout" => Pick("抓包启动超时，请检查游戏与网卡", "抓包啟動逾時，請檢查遊戲與網卡"),
        "observation.sourceEnded" => Pick("抓包已结束，可重新开始", "抓包已結束，可重新開始"),
        "input.bindingUnavailable" => Pick("请先选择游戏窗口", "請先選擇遊戲視窗"),
        "input.bindingChanged" => Pick("游戏窗口已变化，请重新开始", "遊戲視窗已變化，請重新開始"),
        "resource.busy" => Pick("抓包正用于其他窗口或网卡", "抓包正用於其他視窗或網卡"),
        "resource.capacityExceeded" => Pick("抓包使用者已达上限", "抓包使用者已達上限"),
        "permission.denied" => Pick("没有抓包权限", "沒有抓包權限"),
        { } other when other.StartsWith("worker.", StringComparison.Ordinal) =>
            Pick("观测组件异常结束：", "觀測元件異常結束：") + Localization.CodeText.Reason(other),
        _ => null
    };

    /// <summary>房间协议码（原工具 Friendly）。</summary>
    public static string? Protocol(string? code) => code switch
    {
        "BAD_NICKNAME" => Pick("昵称需 1–20 字", "暱稱需 1–20 字"),
        "MEMBER_NOT_READY" => Pick("有选中的成员未就绪", "有選中的成員未就緒"),
        "NO_ENSEMBLE_INITIATOR" => Pick("请指定并勾选至少一位乐谱使用者", "請指定並勾選至少一位樂譜使用者"),
        "CANNOT_KICK_HOST" => Pick("房主退出请点「关闭房间」", "房主退出請點「關閉房間」"),
        "BAD_ENSEMBLE_GROUP" => Pick("组号应为 1–24", "組號應為 1–24"),
        "BAD_ENSEMBLE_ORDER" => Pick("顺序应为 1–24 且组内连续", "順序應為 1–24 且組內連續"),
        "BAD_INSTRUMENT" => Pick("乐器名称最多 40 字", "樂器名稱最多 40 字"),
        "MEMBER_NOT_FOUND" => Pick("该成员已离开房间", "該成員已離開房間"),
        "CHARACTER_NOT_OBSERVED" => Pick("暂未识别该角色，请稍后重试", "暫未辨識該角色，請稍後重試"),
        "CAPTURE_IDENTITY_CONFLICT" => Pick("已自动识别角色，无需匹配", "已自動辨識角色，無需匹配"),
        "FORBIDDEN" => Pick("该操作由房主执行", "該操作由房主執行"),
        "SESSION_ACTIVE" => Pick("本轮仍在进行，请先取消或全停", "本輪仍在進行，請先取消或全停"),
        "USE_STOP_ALL" => Pick("已过开始时刻，请用全员停止", "已過開始時刻，請用全員停止"),
        "LEADER_NOT_PARTICIPATING" => Pick("总指挥需加入本轮", "總指揮需加入本輪"),
        "STOP_NOT_APPLICABLE" => Pick("表情动作无需设置停止", "表情動作無需設定停止"),
        "COUNTDOWN_BEFORE_LOCAL_TRIGGER" => Pick("倒计时短于成员提前量，请加长", "倒數短於成員提前量，請加長"),
        "ROOM_NOT_FOUND" or "SESSION_EXPIRED" => Pick("房间已失效，请重新创建或加入", "房間已失效，請重新建立或加入"),
        "ROOM_FULL" => Pick("房间已满", "房間已滿"),
        "RATE_LIMIT" => Pick("操作太快，请稍后再试", "操作太快，請稍後再試"),
        "BAD_PARTICIPANTS" => Pick("成员有变化，请重新选择参与者", "成員有變化，請重新選擇參與者"),
        "BAD_COUNTDOWN" => Pick("倒计时需 1–60 秒", "倒數需 1–60 秒"),
        "BAD_DURATION" => Pick("演奏时长需 0.001–86400 秒", "演奏時長需 0.001–86400 秒"),
        "BAD_STOP_RULE" => Pick("停止方式无效，请重新选择", "停止方式無效，請重新選擇"),
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
        _ => (Pick("本轮命令结果待确认", "本輪命令結果待確認"), RoomTone.Warning)
    };

    /// <summary>
    /// 本机停止的结果（<c>RoomLocalStopResult.localAction</c>）：已排入停止键、无法准备停止、已取消本机待执行动作，
    /// 或没有可停止的任务。房间已确认时说明由房主决定全员停止或继续。
    /// </summary>
    public static (string Text, RoomTone Tone) LocalStop(string localAction, bool serverConfirmed) => localAction switch
    {
        "stopScheduled" => (serverConfirmed
            ? Pick("已发出本机停止，等房主决定", "已送出本機停止，等房主決定")
            : Pick("已发出本机停止，房间未确认", "已送出本機停止，房間未確認"), serverConfirmed ? RoomTone.Success : RoomTone.Warning),
        "stopRejected" => (Pick("本机停止失败，请在游戏中手动停止", "本機停止失敗，請在遊戲中手動停止"), RoomTone.Error),
        "cancelled" => (Pick("已取消本机任务", "已取消本機任務"), RoomTone.Success),
        _ => (Pick("本机没有待执行的任务", "本機沒有待執行的任務"), RoomTone.Info)
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
