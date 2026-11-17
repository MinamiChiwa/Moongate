using System.Diagnostics;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>
/// 状态卡与各卡片随状态变化的部分：阶段、下一步、身份摘要、按钮可见与可用。
/// 文案与条件取原工具 MainWindow.xaml.cs（RefreshFlow、RefreshStatusView）、Workflow.cs、Music.cs、Ensemble.cs、Status.cs。
/// <see cref="Countdown"/> 是状态卡的倒计时进度，<see cref="ScoreProgress"/> 是总指挥乐谱的估计播放进度；二者与阶段文字里的剩余秒数
/// 都按 <see cref="RoomsPageData.Timing"/> 在给定本机单调时刻换算，信息浮窗可直接读取。
/// </summary>
public sealed record RoomStatus(
    string Phase, RoomTone PhaseTone, string NextStep, string RoleSummary, string RoleLine, string MusicStatus, string ScoreCard,
    bool ShowReady, bool ReadyOn, bool CanReady,
    bool ShowStart, bool CanStart,
    bool ShowCancel, bool CanCancel,
    bool ShowLocalStop, string LocalStopLabel, bool CanLocalStop,
    bool ShowStopAll, bool CanStopAll,
    bool ShowReport, bool CanReport,
    bool ShowContinue, string ContinueLabel, bool CanContinue,
    bool Active, bool Joiner, bool CanEditRoster, bool CanChangeMode, bool CanEditHostSettings, bool CanEditLocal,
    string CountdownNote, string TailRoundText, string BindingError,
    RoomCountdown? Countdown = null, RoomScoreProgress? ScoreProgress = null, string StartIssue = "", string StartHint = "");

public static class RoomFlow
{
    /// <summary>
    /// 演奏开始后只保留一条进度：有乐谱进度条时不再显示“距离统一停止”的倒计时进度；
    /// 没有乐谱时长时仍显示倒计时进度，到停止时刻后也不再显示（<see cref="RoomClock.Countdown"/>）。开始前的“距离同步开始”不受影响。
    /// </summary>
    public static RoomCountdown? SingleBar(RoomCountdown? countdown, RoomScoreProgress? score) =>
        countdown is { Stopping: true } && score?.Fraction is not null ? null : countdown;

    /// <summary>
    /// 统一停止到点后的收尾状态，合奏页与信息浮窗的阶段文字共用：本机停止失败写明原因；已执行时看其余参与者是否都有停止回执，
    /// 未齐写“等待全员停止”；到点但本机回执未到写“正在执行停止”。本轮没有统一停止或还没到点时为空。
    /// </summary>
    public static (string Text, RoomTone Tone)? WrapUp(RoomRound round, string? selfId, RoomTiming? timing, long now)
    {
        if (round.StopActionId is not { } stopId || !round.Active) return null;
        var stopped = round.Receipts.Where(item => item.ActionId == stopId).ToList();
        var mine = stopped.LastOrDefault(item => item.MemberId == selfId);
        if (mine is null && !(round.StopCommitted && timing?.StopRemaining(now) is <= 0)) return null;
        var pending = round.ParticipantIds.Count(id => stopped.All(item => item.MemberId != id));
        var waiting = pending > 0 ? Pick($" · 等待全员停止（还有 {pending} 人）", $" · 等待全員停止（還有 {pending} 人）") : string.Empty;
        return mine?.Status switch
        {
            null when round.ParticipantIds.Contains(selfId ?? string.Empty) => (Pick("正在执行停止", "正在執行停止") + waiting, RoomTone.Info),
            null => (Pick("等待全员停止", "等待全員停止") + (pending > 0 ? Pick($"（还有 {pending} 人）", $"（還有 {pending} 人）") : string.Empty), RoomTone.Info),
            "executed" => (Pick("已执行停止", "已執行停止") + waiting, RoomTone.Success),
            "cancelled" => (Pick("本机停止已取消", "本機停止已取消") + waiting, RoomTone.Warning),
            _ => (Pick("停止失败：", "停止失敗：") + (Backend.RoomReadinessText.Reason(mine.Detail) ?? Localization.CodeText.State(mine.Status))
                  + Pick("，请在游戏中手动停止", "，請在遊戲中手動停止"), RoomTone.Error)
        };
    }

    /// <summary>
    /// 本机停止动作（指定时长、总指挥一轮或全员停止到点后对游戏按下的停止键或按钮）的结果：已执行，或失败并写明原因；
    /// 本轮还没有本机停止回执时为空。游戏内演奏会自动循环，停止没有送达时要让用户知道需要手动停止。
    /// </summary>
    public static string StopReceiptText(RoomRound round, string? selfId)
    {
        var receipt = round.Receipts.LastOrDefault(item => item.MemberId == selfId && item.ActionId != round.StartActionId);
        return receipt?.Status switch
        {
            null => string.Empty,
            "executed" => Pick(" 本机停止操作已执行，请确认游戏中已停止演奏。", " 本機停止操作已執行，請確認遊戲中已停止演奏。"),
            "cancelled" => Pick(" 本机停止操作已取消。", " 本機停止操作已取消。"),
            _ => Pick(" 本机停止操作没有执行：", " 本機停止操作沒有執行：")
                 + (Backend.RoomReadinessText.Reason(receipt.Detail) ?? Localization.CodeText.State(receipt.Status))
                 + Pick("。请在游戏中手动停止演奏。", "。請在遊戲中手動停止演奏。")
        };
    }

    /// <param name="mode">当前模式：房间里取房间模式，未入房取界面上的选择。</param>
    /// <param name="preferences">按原工具默认值补齐后的本机设置；未连接时为空。</param>
    /// <param name="settingsInvalid">倒计时、指定时长或尾音保留的输入不合法，开始停用。</param>
    /// <param name="now">换算剩余时间的本机单调时刻（<see cref="Stopwatch.GetTimestamp"/>）；为空时取此刻。</param>
    public static RoomStatus Describe(RoomsPageData data, string mode, RoomPreferences? preferences, bool settingsInvalid = false, long? now = null)
    {
        var at = now ?? Stopwatch.GetTimestamp();
        var timing = data.Timing;
        var room = data.Room;
        var inRoom = room is not null;
        var connected = data.RoomConnected;
        var host = data.IsHost;
        var music = mode != "emote";
        var self = data.Self;
        var round = room?.Round;
        var active = round is { Active: true };
        var joiner = mode == "ensemble" && self is { Initiator: false };
        var participating = !active || round!.ParticipantIds.Contains(data.SelfId ?? string.Empty);
        var bindingError = preferences is null ? string.Empty : RoomLocalSettings.BindingError(preferences, mode, joiner) ?? string.Empty;
        string Name(string? id) => room?.Members.FirstOrDefault(member => member.Id == id)?.Nickname ?? Pick("未指定", "未指定");

        // 身份摘要（Workflow.RefreshRoleLayout）。
        var roleSummary = !inRoom ? Pick("创建房间，或接受好友邀请", "建立房間，或接受好友邀請")
            : mode == "ensemble" && self is not null
                ? Pick($"{(host ? "房主" : "成员")} · 第 {self.Group} 组 · {(self.Initiator ? "乐谱使用者" : "参与者")} · 第 {self.Order} 位 · 乐器：{InstrumentText(self.Instrument)}",
                    $"{(host ? "房主" : "成員")} · 第 {self.Group} 組 · {(self.Initiator ? "樂譜使用者" : "參與者")} · 第 {self.Order} 位 · 樂器：{InstrumentText(self.Instrument)}")
                : host ? Pick("房主 · 管理本轮参与者与开始时间", "房主 · 管理本輪參與者與開始時間") : Pick("成员 · 准备本机动作", "成員 · 準備本機動作");

        // 本机动作卡的身份说明（Ensemble.RefreshEnsembleRoster）。
        var roleLine = mode != "ensemble" ? string.Empty
            : !inRoom ? Pick("入房后由房主分组并指定乐谱使用者。", "入房後由房主分組並指定樂譜使用者。")
            : self is null ? Pick("等待房间成员信息。", "等待房間成員資訊。")
            : joiner ? Pick($"第 {self.Group} 组 · 第 {self.Order} 位加入。设置停止按键或按钮后点击“就绪”，等乐谱使用者开始后在游戏中加入。",
                $"第 {self.Group} 組 · 第 {self.Order} 位加入。設定停止按鍵或按鈕後點擊「就緒」，等樂譜使用者開始後在遊戲中加入。")
            : Pick($"第 {self.Group} 组 · 乐谱使用者。在背包中选好乐谱，设置乐谱使用按钮与停止快捷键。",
                $"第 {self.Group} 組 · 樂譜使用者。在背包中選好樂譜，設定樂譜使用按鈕與停止快捷鍵。");

        // 阶段（RefreshStatusView）。本轮结束或没有本轮时显示就绪情况。
        string phase;
        var tone = RoomTone.Info;
        var selected = room?.Members.Where(member => member.Selected).ToList() ?? [];
        var waiting = selected.Where(member => member.Id != data.SelfId && !(member.Ready && member.Connected)).Select(member => member.Nickname).ToList();
        var selfReady = self is { Ready: true };
        if (!data.Connected && !inRoom) phase = Pick("连接房间", "連線房間");
        else if (!inRoom) phase = Pick("连接房间", "連線房間");
        else if (!connected) { phase = Pick("房间连接已中断", "房間連線已中斷"); tone = RoomTone.Error; }
        else if (!active)
        {
            // 房主免就绪（原工具 IdleReadinessText）：房主看勾选参与的其他成员是否都已就绪。
            phase = host
                ? waiting.Count > 0 ? Pick("等待全员就绪", "等待全員就緒") : Pick("全员已就绪", "全員已就緒")
                : selfReady ? Pick("已就绪", "已就緒") : Pick("未就绪", "未就緒");
            tone = phase == Pick("已就绪", "已就緒") || phase == Pick("全员已就绪", "全員已就緒") ? RoomTone.Success
                : host && phase != Pick("未就绪", "未就緒") ? RoomTone.Info : RoomTone.Error;
        }
        else
        {
            var receipt = round!.Receipts.LastOrDefault(item => item.ActionId == round.StartActionId && item.MemberId == data.SelfId);
            var wrapUp = WrapUp(round, data.SelfId, timing, at);
            phase = round.Phase switch
            {
                "committed" or "performing" when wrapUp is { } wrap => wrap.Text,
                "preparing" => Pick("正在准备", "正在準備"),
                "committed" or "performing" when joiner => Pick("等待组内加入", "等待組內加入"),
                "committed" when receipt is not null => receipt.Status switch
                {
                    "executed" => Pick("本机操作已执行", "本機操作已執行"),
                    "failed" => Pick("本机操作失败", "本機操作失敗"),
                    "missed" => Pick("已错过执行时刻", "已錯過執行時刻"),
                    _ => Pick("本机操作已结束，请查看执行记录", "本機操作已結束，請查看執行紀錄")
                },
                "committed" when timing?.StartRemaining(at) is { } remaining => (round.Mode == "emote", data.Simulation) switch
                {
                    (true, true) => Pick($"本机模拟表情将在 {TriggerSeconds(remaining)} 秒后触发", $"本機模擬表情將在 {TriggerSeconds(remaining)} 秒後觸發"),
                    (true, false) => Pick($"本机表情将在 {TriggerSeconds(remaining)} 秒后触发", $"本機表情將在 {TriggerSeconds(remaining)} 秒後觸發"),
                    (false, true) => Pick($"本机模拟演奏将在 {TriggerSeconds(remaining)} 秒后触发", $"本機模擬演奏將在 {TriggerSeconds(remaining)} 秒後觸發"),
                    _ => Pick($"本机演奏将在 {TriggerSeconds(remaining)} 秒后触发", $"本機演奏將在 {TriggerSeconds(remaining)} 秒後觸發")
                },
                "committed" => Pick("任务已安排", "任務已安排"),
                "performing" => Pick("本轮进行中", "本輪進行中"),
                "interrupted" => Pick("演奏中断", "演奏中斷"),
                _ => Pick("等待准备", "等待準備")
            };
            if (round.Phase == "interrupted" || receipt?.Status is "failed" or "missed") tone = RoomTone.Error;
            if (round.Phase is "committed" or "performing" && wrapUp is { } ended) tone = ended.Tone;
        }

        // 下一步（Workflow.RefreshNextStep）。
        string next;
        if (!inRoom) next = Pick("创建房间，或粘贴邀请加入。", "建立房間，或貼上邀請加入。");
        else if (!connected)
            next = host ? Pick("连接已断开，可点击重新连接，或关闭房间。", "連線已中斷，可點擊重新連線，或關閉房間。")
                : Pick("连接已断开，请点击重新连接；恢复后重新就绪。", "連線已中斷，請點擊重新連線；恢復後重新就緒。");
        else if (active)
        {
            next = !participating
                ? host ? Pick("选中的成员正在参与本轮。你可以取消本轮或全员停止。", "選中的成員正在參與本輪。你可以取消本輪或全員停止。")
                    : Pick("你正在旁观本轮。", "你正在旁觀本輪。")
                : round!.Phase == "interrupted"
                    ? host ? Pick("请选择全员停止，或继续演奏。", "請選擇全員停止，或繼續演奏。") : Pick("等待房主决定停止或继续。", "等待房主決定停止或繼續。")
                    : joiner
                        ? Pick("靠近本组乐谱使用者，按成员列表中的顺序加入。", "靠近本組樂譜使用者，按成員列表中的順序加入。")
                        : data.Simulation ? Pick("正在模拟本轮操作，可在“执行记录与诊断”中查看结果。", "正在模擬本輪操作，可在「執行紀錄與診斷」中查看結果。")
                            : Pick("开始前自动激活所选游戏窗口，请保持游戏在前台。", "開始前自動啟用所選遊戲視窗，請保持遊戲在前景。");
        }
        else if (!data.Simulation && data.GameWindowName is null) next = Pick("请在本机动作中选择游戏窗口。", "請在本機動作中選擇遊戲視窗。");
        else if (!data.Simulation && bindingError.Length > 0) next = bindingError;
        else if (!data.Simulation && preferences is not null && music && RoomLocalSettings.Stop(preferences, mode) is null)
            next = Pick("请记录画面上的停止按钮，或设置停止快捷键。", "請記錄畫面上的停止按鈕，或設定停止快捷鍵。");
        else if (!data.Simulation && preferences is not null && !joiner && RoomLocalSettings.Start(preferences, mode) is null)
            next = Pick("请先设置开始按键，或点选游戏中的开始按钮。", "請先設定開始按鍵，或點選遊戲中的開始按鈕。");
        else if (host)
            next = waiting.Count > 0
                    ? Pick("未就绪：", "未就緒：") + string.Join("、", waiting.Take(3)) + (waiting.Count > 3 ? Pick($" 等 {waiting.Count} 人", $" 等 {waiting.Count} 人") : string.Empty)
                    : Pick("点击开始", "點擊開始");
        else next = selfReady ? Pick("等待房主开始", "等待房主開始") : Pick("确认本机设置后点击就绪", "確認本機設定後點擊就緒");

        // 合奏的结束方式说明与总指挥乐谱（Music.RefreshMusicStatus、RefreshScoreObservation）。
        var stopRule = preferences?.MusicStopRule.RawValue ?? "first-cycle";
        var musicStatus = string.Empty;
        if (music && round is not null)
        {
            if (round.Mode == "emote")
                musicStatus = host ? IdleRuleText(stopRule, preferences) : Pick("总指挥与结束方式由房主设置。", "總指揮與結束方式由房主設定。");
            else
            {
                var automatic = round.StopRule == "first-cycle";
                var ending = round.HasStop
                    ? !round.StopCommitted ? Pick("正在确认各成员能否停止演奏", "正在確認各成員能否停止演奏")
                        : timing?.StopRemaining(at) is { } stopIn
                            ? automatic
                                ? Pick($"距离本机停止还有 {RoomClock.SecondsText(stopIn)}（总指挥一轮 · 估计）", $"距離本機停止還有 {RoomClock.SecondsText(stopIn)}（總指揮一輪 · 估計）")
                                : Pick($"距离本机停止还有 {RoomClock.SecondsText(stopIn)}（指定时长）", $"距離本機停止還有 {RoomClock.SecondsText(stopIn)}（指定時長）")
                        : automatic ? Pick("已安排统一停止（总指挥一轮）", "已安排統一停止（總指揮一輪）") : Pick("已安排统一停止（指定时长）", "已安排統一停止（指定時長）")
                    : automatic ? CycleWaitingText(round.CycleStopStatus) : Pick("由房主手动全员停止", "由房主手動全員停止");
                var interruption = round.Interruption is { } detail ? InterruptionText(detail, Name) : string.Empty;
                var failures = round.Receipts.Count(item => item.Status is "failed" or "missed");
                musicStatus = round.Phase switch
                {
                    "interrupted" => Pick($"总指挥：{Name(round.LeaderId)} · {interruption}。{ending}。由房主决定全停或继续。",
                        $"總指揮：{Name(round.LeaderId)} · {interruption}。{ending}。由房主決定全停或繼續。"),
                    "cancelled" => Pick($"本轮已取消：{interruption}。下一轮请重新准备并就绪。", $"本輪已取消：{interruption}。下一輪請重新準備並就緒。"),
                    "stopped" => (failures > 0 ? Pick($"有 {failures} 次操作未执行成功，请查看“执行记录与诊断”并检查游戏。", $"有 {failures} 次操作未執行成功，請查看「執行紀錄與診斷」並檢查遊戲。") : string.Empty)
                        + Pick("本轮已结束，请确认游戏中已停止演奏。下一轮请重新就绪。", "本輪已結束，請確認遊戲中已停止演奏。下一輪請重新就緒。"),
                    _ => Pick($"总指挥：{Name(round.LeaderId)} · {ending}。", $"總指揮：{Name(round.LeaderId)} · {ending}。")
                };
                musicStatus += StopReceiptText(round, data.SelfId);
            }
        }
        var scoreCard = string.Empty;
        RoomScoreProgress? scoreProgress = null;
        if (music && inRoom && connected)
        {
            var conductorId = active ? round!.LeaderId : room!.LeaderId;
            var conductor = room!.Members.FirstOrDefault(member => member.Id == conductorId);
            scoreProgress = RoomClock.ScoreProgress(round, conductor?.Music, timing, at);
            scoreCard = conductor?.Music is { } score
                ? Pick("总指挥乐谱：", "總指揮樂譜：") + (score.Title.Trim().Length == 0 ? Pick("未命名乐谱", "未命名樂譜") : score.Title) + " · "
                  + (score.WholeSeconds is { } whole ? Pick($"完整乐谱 {RoomJson.Seconds(whole)} 秒", $"完整樂譜 {RoomJson.Seconds(whole)} 秒") : Pick("时长待解析", "時長待解析"))
                  + (score.SelectedSeconds is { } part ? Pick($" · 当前音轨 {RoomJson.Seconds(part)} 秒", $" · 目前音軌 {RoomJson.Seconds(part)} 秒") : string.Empty)
                : Pick("乐谱：等待识别", "樂譜：等待辨識");
        }

        // 按钮（Workflow.RefreshRoleLayout、RefreshFlow、Music.RefreshMusicStatus）。
        // 房主不显示就绪：开始时由后端按已保存的本机设置准备房主的输入。
        // 开始按钮与原工具 RefreshFlow 一致：房主、已连接、没有进行中的本轮、倒计时与停止参数有效、上一次开始或就绪已有结果时可点；
        // 参与者、就绪和本机设置在点击时核对（原工具 PrepareClick 与 ValidateLocalReadiness），不满足时在反馈横幅说明（StartIssue）。
        var roomUsable = inRoom && connected;
        var showReady = inRoom && !host;
        var bindingUsable = bindingError.Length == 0 || data.Simulation;
        var canReady = roomUsable && !active && bindingUsable && !data.RoundBusy;
        var canStart = host && roomUsable && !active && !settingsInvalid && !data.RoundBusy;
        var selfSelected = self is { Selected: true };
        var startIssue = !host || !roomUsable || active ? string.Empty
            : selected.Count == 0 ? Pick("请先选择本轮参与者。", "請先選擇本輪參與者。")
            : waiting.Count > 0 ? Pick("等待就绪：", "等待就緒：") + string.Join("、", waiting) + Pick("。", "。")
            : !selfSelected || data.Simulation ? string.Empty
            : data.GameWindowName is null ? Pick("请先在本机动作中选择游戏窗口。", "請先在本機動作中選擇遊戲視窗。")
            : bindingError.Length > 0 ? bindingError
            : preferences is not null && music && RoomLocalSettings.Stop(preferences, mode) is null
                ? Pick("请记录画面上的停止按钮，或设置停止快捷键。", "請記錄畫面上的停止按鈕，或設定停止快捷鍵。")
            : preferences is not null && !joiner && RoomLocalSettings.Start(preferences, mode) is null
                ? Pick("请先设置开始按键，或点选游戏中的开始按钮。", "請先設定開始按鍵，或點選遊戲中的開始按鈕。")
            : string.Empty;
        // 开始停用时的原因；本轮进行中与未连接由阶段文字说明。
        var startHint = !host || !roomUsable || active ? string.Empty
            : data.RoundBusy ? Pick("正在等待上一次操作的结果…", "正在等待上一次操作的結果…")
            : settingsInvalid ? Pick("倒计时、演奏时长或尾音保留填写有误，修正后才能开始。", "倒數、演奏時長或尾音保留填寫有誤，修正後才能開始。")
            : string.Empty;
        var beforeStart = active && round!.Phase is "preparing" or "committed";
        var startReached = active && (round!.Phase is "performing" or "interrupted"
                                      || round.Receipts.Any(item => item.ActionId == round.StartActionId));
        var isConductor = active && round!.LeaderId == data.SelfId;
        var emoteRound = active ? round!.Mode == "emote" : !music;
        var pendingStart = data.LocalPreparationId is not null && beforeStart;
        var showLocalStop = active && participating;
        var showContinue = host && active && round!.Phase == "interrupted" && !emoteRound;
        var countdownNote = active && round!.CountdownMs > 0
            ? Pick($"本轮倒计时 {RoomLocalSettings.Seconds(round.CountdownMs)} 秒。", $"本輪倒數 {RoomLocalSettings.Seconds(round.CountdownMs)} 秒。")
              + (round.RequestedCountdownMs > 0 && round.RequestedCountdownMs < round.CountdownMs
                  ? Pick(" 已自动延长倒计时，为提前触发和网络传输留出时间。", " 已自動延長倒數，為提前觸發和網路傳輸留出時間。") : string.Empty)
            : string.Empty;
        var tailRound = active ? Pick($"本轮 {RoomLocalSettings.Seconds(round!.TailHoldMs)} 秒", $"本輪 {RoomLocalSettings.Seconds(round!.TailHoldMs)} 秒")
            : Pick("由房主设置", "由房主設定");

        return new(phase, tone, next, roleSummary, roleLine, musicStatus, scoreCard,
            showReady, selfReady, canReady,
            host, canStart,
            host && beforeStart, roomUsable,
            showLocalStop, emoteRound ? Pick("取消本机任务", "取消本機任務") : Pick("本机停止", "本機停止"),
            roomUsable && (emoteRound ? pendingStart : showLocalStop),
            host && active && !emoteRound, roomUsable,
            isConductor && startReached && !emoteRound && round!.Phase != "interrupted", roomUsable,
            showContinue, round is { HasStop: true, StopCommitted: true } ? Pick("继续，按原计划停止", "繼續，按原計畫停止") : Pick("继续，稍后手动停止", "繼續，稍後手動停止"),
            roomUsable,
            active, joiner, host && roomUsable && !active && mode == "ensemble", !active && (!inRoom || host && roomUsable),
            !active && (!inRoom || host), !active,
            countdownNote, tailRound, bindingError,
            SingleBar(RoomClock.Countdown(round, timing, at), scoreProgress), scoreProgress, startIssue, startHint);
    }

    private static string TriggerSeconds(double remainingMs) =>
        Math.Max(0, remainingMs / 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>未进行本轮时房主看到的结束方式说明。</summary>
    public static string IdleRuleText(string stopRule, RoomPreferences? preferences) => stopRule switch
    {
        "duration" => Pick($"结束方式：演奏 {RoomLocalSettings.Seconds(preferences?.MusicDurationMs ?? 30000)} 秒后全员停止。",
            $"結束方式：演奏 {RoomLocalSettings.Seconds(preferences?.MusicDurationMs ?? 30000)} 秒後全員停止。"),
        "first-cycle" => Pick("总指挥需开启抓包识别。工具根据乐谱和开始时间估算一轮结束时间，届时全员停止。",
            "總指揮需開啟抓包辨識。工具根據樂譜和開始時間估算一輪結束時間，屆時全員停止。"),
        _ => Pick("结束方式：房主手动全员停止。", "結束方式：房主手動全員停止。")
    };

    /// <summary>停止方式旁的说明（原工具 LeaderPurposeText）。</summary>
    public static string LeaderPurpose(string stopRule) => stopRule switch
    {
        "first-cycle" => Pick("按总指挥的乐谱估算一轮终点，保留尾音后全员停止。尾音保留由房主设置；总指挥提前结束时会提醒房主。",
            "按總指揮的樂譜估算一輪終點，保留尾音後全員停止。尾音保留由房主設定；總指揮提前結束時會提醒房主。"),
        "duration" => Pick("显示总指挥的乐谱；总指挥提前结束时可通知房主。全员按指定时长停止。", "顯示總指揮的樂譜；總指揮提前結束時可通知房主。全員按指定時長停止。"),
        _ => Pick("显示总指挥的乐谱；总指挥提前结束时可通知房主。何时全员停止由房主决定。", "顯示總指揮的樂譜；總指揮提前結束時可通知房主。何時全員停止由房主決定。")
    };

    public static string InstrumentText(string instrument) => instrument.Length == 0 ? Pick("未指定", "未指定") : instrument;

    private static string CycleWaitingText(string? status) => status switch
    {
        "too-late" => Pick("识别乐谱时已接近一轮结束，来不及安排自动停止，请房主手动全员停止", "辨識樂譜時已接近一輪結束，來不及安排自動停止，請房主手動全員停止"),
        "participant-not-ready" => Pick("有成员尚未设置好停止操作，请房主手动全员停止", "有成員尚未設定好停止操作，請房主手動全員停止"),
        "prepare-timeout" => Pick("未能及时确认所有成员的停止设置，请房主手动全员停止", "未能及時確認所有成員的停止設定，請房主手動全員停止"),
        "interrupted-before-stop-commit" => Pick("尚未安排统一停止，请房主选择继续或全员停止", "尚未安排統一停止，請房主選擇繼續或全員停止"),
        null => Pick("等待识别总指挥的乐谱和开始时间", "等待辨識總指揮的樂譜和開始時間"),
        _ => Pick("等待识别总指挥本轮的乐谱和开始时间；房主可随时全员停止", "等待辨識總指揮本輪的樂譜和開始時間；房主可隨時全員停止")
    };

    private static string InterruptionText(RoomInterruption detail, Func<string?, string> name)
    {
        var who = detail.MemberId is { } id ? name(id) : Pick("本轮", "本輪");
        if (detail.EvidenceSource == "player-report") return Pick($"{who} 手动报告已提前停止", $"{who} 手動回報已提前停止");
        var reason = detail.Source switch
        {
            "tool-connection" or "tool-session-replaced" => Pick("工具连接中断，请确认游戏状态。", "工具連線中斷，請確認遊戲狀態。"),
            "participant-not-ready" => Pick("按键或按钮尚未设置好", "按鍵或按鈕尚未設定好"),
            "input-failure" => Pick("操作失败或未能按时执行", "操作失敗或未能按時執行"),
            "not-ready" => Pick("取消就绪", "取消就緒"),
            "local-stop" => Pick("执行本机停止", "執行本機停止"),
            "prepare-timeout" or "prepare-too-late" => Pick("未能及时准备好", "未能及時準備好"),
            "host-cancel" => Pick("房主取消", "房主取消"),
            "member-left" => Pick("离开房间", "離開房間"),
            "game-observation" => Pick("报告演奏中断", "回報演奏中斷"),
            _ => Pick("本轮已取消或中断", "本輪已取消或中斷")
        };
        return who + Pick("：", "：") + reason + (detail.Detail is { Length: > 0 } extra ? " · " + extra : string.Empty);
    }
}
