using System.Globalization;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>运行栏状态行与说明行：标题是当前阶段或结果，说明是阶段指引、读数、结果解释与下一步。</summary>
public sealed record ColorRunStatus(string Title, string Detail, ColorTone Tone);

/// <summary>
/// 寻色状态的中文呈现，按原工具 Color Studio 0.5.0 的 ui_progress.progress_text、native_status 与 screenshot_presentation：
/// 进行中按阶段、引擎说明、细化状态与重试状态给出标题与说明；结束后按宿主停止原因、引擎停止原因与细分原因给出结论和用户可做的事。
/// 引擎说明是简体原文，繁体界面按对照表换写；未登记的阶段与原因码原样显示，不留空白。
/// </summary>
public static class ColorRunText
{
    /// <summary>运行已结束的阶段（宿主写入的终态名）。</summary>
    public static bool IsActive(ColorRun run) => run.Phase is not ("succeeded" or "failed" or "cancelled" or "interrupted");

    public static ColorRunStatus Describe(ColorRun run)
    {
        if (IsActive(run)) return Running(run);
        var (title, detail) = Ended(run);
        var tone = run.Phase is "failed" or "interrupted" ? ColorTone.Warn : ColorTone.Idle;
        return new(title, detail, tone);
    }

    // ---- 进行中 ----

    private static ColorRunStatus Running(ColorRun run)
    {
        if (run.Phase == "stopping") return new(Pick("正在停止", "正在停止"), Pick("正在停止输入并收尾；请以游戏当前颜色为准。", "正在停止輸入並收尾；請以遊戲目前顏色為準。"), ColorTone.Live);
        if (run.Progress is not { } progress)
            return new(Pick("正在启动寻色", "正在啟動尋色"),
                Pick("正在把游戏窗口切到前台并启动染色引擎，首次启动需要几秒。F9 可停止。",
                    "正在把遊戲視窗切到前景並啟動染色引擎，首次啟動需要幾秒。F9 可停止。"), ColorTone.Live);
        var regions = run.RulesSnapshot.Regions;
        switch (progress.Status)
        {
            case "retry-waiting":
                return new(Pick("本轮寻色已完成", "本輪尋色已完成"), Join(Lines(regions, progress.Reading, Pick("当前配色 · 游戏实测", "目前配色 · 遊戲實測")),
                    run.Interaction?.RetryEnabled == true
                        ? Pick("可点「重试」在本局剩余时间内重新寻色；剩余时间不足时保留当前颜色。满意时点「停止」。",
                            "可點「重試」在本局剩餘時間內重新尋色；剩餘時間不足時保留目前顏色。滿意時點「停止」。")
                        : RetryReason(progress.Reason)), ColorTone.Live);
            case "retry-zooming":
                return new(Pick("正在重试", "正在重試"), Pick("正在缩小画板并重新搜索。", "正在縮小畫板並重新搜尋。"), ColorTone.Live);
            case "retry-searching":
                return new(Pick("重新搜索目标颜色", "重新搜尋目標顏色"), Pick("沿用本局剩余时间重新寻色；F9 可停止。", "沿用本局剩餘時間重新尋色；F9 可停止。"), ColorTone.Live);
            case "retry-rejected":
                return new(Pick("暂时无法重试", "暫時無法重試"), RetryReason(progress.Reason), ColorTone.Live);
        }
        if (progress.Stage == "refinement") return Refinement(regions, progress);
        var title = ColorStages.Name(progress.Stage);
        if (progress.Total is > 0 and var total)
            title += "  " + (progress.Current ?? 0).ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
        var body = progress.Message is { } message ? Message(message) : DefaultBody(progress.Stage);
        return new(title, Join(body, Lines(regions, progress.Reading, Pick("当前颜色 · 游戏读数", "目前顏色 · 遊戲讀數"))), ColorTone.Live);
    }

    private static ColorRunStatus Refinement(IReadOnlyList<ColorRegion> regions, ColorRunProgress progress)
    {
        var status = progress.Status;
        var trying = status is "trial" or "restoring" or "recovering";
        var current = Lines(regions, progress.Reading, status == "trial" ? Pick("当前试探 · 游戏实测", "目前試探 · 遊戲實測") : Pick("当前配色 · 游戏实测", "目前配色 · 遊戲實測"));
        var retained = trying ? Lines(regions, progress.Retained, Pick("已保留配色 · 游戏实测", "已保留配色 · 遊戲實測")) : string.Empty;
        var message = status switch
        {
            "trial" => Pick("正在核对微调结果；尚未替换已保留配色。", "正在核對微調結果；尚未替換已保留配色。"),
            "restoring" => Pick("本次微调没有改善，正在尝试返回已保留配色。", "本次微調沒有改善，正在嘗試返回已保留配色。"),
            "recovering" => Pick("返回出现偏差，正在重新定位并修正配色。", "返回出現偏差，正在重新定位並修正配色。"),
            "retained" => Pick("微调已改善，已保留本次实测配色。", "微調已改善，已保留本次實測配色。"),
            _ => Pick("正在计算更好的配色，计算期间色板可能暂时不动。", "正在計算更好的配色，計算期間色板可能暫時不動。")
        };
        var guidance = trying ? Pick("按 F9 会立即停止操作；请以游戏当前颜色为准。", "按 F9 會立即停止操作；請以遊戲目前顏色為準。")
            : current.Length > 0 || retained.Length > 0 ? Pick("可查看上方已确认的颜色；满意时按 F9 或点「停止」。", "可查看上方已確認的顏色；滿意時按 F9 或點「停止」。")
            : Pick("正在寻找目标颜色。可按 F9 停止，再查看游戏当前结果。", "正在尋找目標顏色。可按 F9 停止，再查看遊戲目前結果。");
        return new(Pick("正在优化当前配色", "正在最佳化目前配色"), Join(current, retained, message, guidance), ColorTone.Live);
    }

    /// <summary>引擎没有给出说明时各阶段的默认说明（原 progress_text 的缺省文字）。</summary>
    internal static string DefaultBody(string? stage) => stage switch
    {
        "discovery" => Pick("正在准备寻色，请保持游戏窗口可见。F9 可停止。", "正在準備尋色，請保持遊戲視窗可見。F9 可停止。"),
        "waiting" => Pick("请在游戏内进入染色；已经打开色板时无需重新进入。F9 可停止。", "請在遊戲內進入染色；已經打開色板時無需重新進入。F9 可停止。"),
        "align" or "period" or "stitch" or "export" or "validate" or "search" or "similarity" or "ready" =>
            Pick("正在计算目标位置，色板暂时不动。F9 可停止。", "正在計算目標位置，色板暫時不動。F9 可停止。"),
        _ => Pick("正在调整色板，请保持游戏在前台。F9 可停止。", "正在調整色板，請保持遊戲在前景。F9 可停止。")
    };

    /// <summary>重试不可用或被拒绝的原因（原 native_status.retry_reason_text）。</summary>
    internal static string RetryReason(string? reason) => reason switch
    {
        "insufficient_retry_time" => Pick("剩余时间不足以完成重试，已保留当前颜色。", "剩餘時間不足以完成重試，已保留目前顏色。"),
        "insufficient_retry_actions" => Pick("剩余操作次数不足以完成重试，已保留当前颜色。", "剩餘操作次數不足以完成重試，已保留目前顏色。"),
        "action_limit" => Pick("本局自动操作次数已达上限，已保留当前颜色。", "本局自動操作次數已達上限，已保留目前顏色。"),
        "round_limit" => Pick("本局自动搜索次数已达上限，已保留当前颜色。", "本局自動搜尋次數已達上限，已保留目前顏色。"),
        "unsupported_retry_settings" => Pick("无法确认当前画板的缩小路线，已保留当前颜色。", "無法確認目前畫板的縮小路線，已保留目前顏色。"),
        "retry_recovery_unavailable" => Pick("无法确认重试后的恢复路线，已保留当前颜色。", "無法確認重試後的恢復路線，已保留目前顏色。"),
        "retry_resetting" => Pick("当前动作尚未结束，请稍候再重试。", "目前動作尚未結束，請稍候再重試。"),
        "session_closing" => Pick("本局正在结束，已保留当前颜色。", "本局正在結束，已保留目前顏色。"),
        _ => Pick("当前状态暂时无法重试，已保留当前颜色。", "目前狀態暫時無法重試，已保留目前顏色。")
    };

    // ---- 结束 ----

    private static (string Title, string Detail) Ended(ColorRun run)
    {
        var result = run.Result;
        if (run.Phase == "cancelled" && run.StopReason is not ("interrupted" or "palette_ended"))
            return run.StopReason switch
            {
                "frontend-lost" => (Pick("界面失联，寻色已停止", "介面失聯，尋色已停止"),
                    Pick("后端超过 5 秒没有收到 Moongate 界面的续约，已停止游戏输入。请以游戏当前颜色为准，确认后重新开始。",
                        "後端超過 5 秒沒有收到 Moongate 介面的續約，已停止遊戲輸入。請以遊戲目前顏色為準，確認後重新開始。")),
                "emergency-stop" => (Pick("已紧急停止", "已緊急停止"), Pick("已停止全部游戏输入；请以游戏当前颜色为准。", "已停止全部遊戲輸入；請以遊戲目前顏色為準。")),
                "host-stopped" => (Pick("后端已停止，寻色结束", "後端已停止，尋色結束"),
                    Pick("后端关闭或重启时停止了寻色；请以游戏当前颜色为准。", "後端關閉或重啟時停止了尋色；請以遊戲目前顏色為準。")),
                _ => (Pick("已停止寻色", "已停止尋色"), Pick("自动操作已停止；请以游戏当前颜色为准，在游戏内决定是否套用。", "自動操作已停止；請以遊戲目前顏色為準，在遊戲內決定是否套用。"))
            };
        // 一局开始前就结束：宿主或 Worker 只给出错误码。
        if (result is null || run.StopReason is { } code && code.Contains('.'))
            return Code(run.StopReason, run.Phase);
        var (title, detail) = run.RulesSnapshot.Strategy.KnownValue == ColorStrategy.Precise ? Native(run, result) : Screenshot(run, result);
        if (result.FailedStage is { } stage && run.Phase is "failed" or "interrupted")
            detail = Join(detail, Pick("停在：", "停在：") + StageName(stage));
        return (title, detail);
    }

    /// <summary>宿主与 Worker 的错误码（一局开始前结束）。</summary>
    internal static (string Title, string Detail) Code(string? code, string phase) => code switch
    {
        "input.focusChanged" => (Pick("游戏失去焦点，已停止寻色", "遊戲失去焦點，已停止尋色"),
            Pick("寻色期间游戏窗口必须保持在前台。请点一下游戏窗口，再点「开始」。", "尋色期間遊戲視窗必須保持在前景。請點一下遊戲視窗，再點「開始」。")),
        "input.bindingChanged" => (Pick("游戏窗口已移动或调整尺寸", "遊戲視窗已移動或調整尺寸"),
            Pick("运行期间窗口位置或尺寸发生变化，已停止按旧坐标操作。确认左上角已绑定当前游戏窗口后重新开始。", "執行期間視窗位置或尺寸發生變化，已停止按舊座標操作。確認左上角已綁定目前遊戲視窗後重新開始。")),
        "input.deadlineExceeded" => (Pick("寻色时限已到", "尋色時限已到"), Pick("本局操作时间已用完，已停止输入；请以游戏当前颜色为准。", "本局操作時間已用完，已停止輸入；請以遊戲目前顏色為準。")),
        "input.runStopped" => (Pick("已停止寻色", "已停止尋色"), Pick("自动操作已停止；请以游戏当前颜色为准。", "自動操作已停止；請以遊戲目前顏色為準。")),
        "resource.busy" => (Pick("游戏输入被其他操作占用", "遊戲輸入被其他操作佔用"), Pick("合奏或其他操作正在使用游戏输入；结束后再开始寻色。", "合奏或其他操作正在使用遊戲輸入；結束後再開始尋色。")),
        "capture.sourceFailed" or "capture.frameInvalid" => (Pick("截取游戏画面失败", "擷取遊戲畫面失敗"),
            Pick("无法截取游戏客户区；确认游戏窗口没有最小化，再重新开始。", "無法擷取遊戲用戶區；確認遊戲視窗沒有最小化，再重新開始。")),
        "permission.missing" => Unavailable("permission.missing"),
        "gameBuild.incompatible" => Unavailable("game_build"),
        _ when phase == "interrupted" => (Pick("染色引擎中断", "染色引擎中斷"),
            Pick($"染色引擎没有正常结束（{code ?? "未知"}），已停止输入；请以游戏当前颜色为准，再重新开始。", $"染色引擎沒有正常結束（{code ?? "未知"}），已停止輸入；請以遊戲目前顏色為準，再重新開始。")),
        _ => (Pick("染色引擎未完成运行", "染色引擎未完成執行"),
            Pick($"原因码 {code ?? "未知"}；详细记录已写入诊断（诊断页可导出），请重新开始。", $"原因碼 {code ?? "未知"}；詳細記錄已寫入診斷（診斷頁可匯出），請重新開始。"))
    };

    /// <summary>截图寻色的一局结果（原 screenshot_presentation.result_text）。</summary>
    private static (string Title, string Detail) Screenshot(ColorRun run, ColorRunResult result)
    {
        var verified = result.CurrentVerified is not null;
        var accepted = result.CurrentVerified?.Accepted == true;
        switch (result.StopReason)
        {
            case "interrupted" when result.StopDetail is "focus_lost" or "window_changed" or "activation_failed":
                return Native(run, result);
            case "interrupted" when verified:
                return (Pick("截图寻色已停止", "截圖尋色已停止"), Pick("游戏实际色码已复核，请在游戏内手动确认是否套用。", "遊戲實際色碼已複核，請在遊戲內手動確認是否套用。"));
            case "palette_ended" or "interrupted":
                return (Pick("截图寻色已停止", "截圖尋色已停止"), Join(Pick("自动操作已停止；请以游戏当前颜色为准。", "自動操作已停止；請以遊戲目前顏色為準。"),
                    result.StopReason == "palette_ended" ? Pick("染色界面已关闭或本局已结束。", "染色介面已關閉或本局已結束。") : string.Empty));
            case "processing_error":
                return (Pick("截图寻色处理异常", "截圖尋色處理異常"), !verified
                    ? Pick("错误记录已保存，当前色码未完成复核；请以游戏当前颜色为准。", "錯誤記錄已儲存，目前色碼未完成複核；請以遊戲目前顏色為準。")
                    : result.BestCurrent == false
                        ? Pick("错误记录已保存，当前颜色已复核，但未能恢复此前较好的结果；请以游戏当前颜色为准。", "錯誤記錄已儲存，目前顏色已複核，但未能恢復此前較好的結果；請以遊戲目前顏色為準。")
                        : Pick("已保留或恢复本轮较好的实测颜色，错误记录已保存；请查看当前色差后再决定是否套用。", "已保留或恢復本輪較好的實測顏色，錯誤記錄已儲存；請查看目前色差後再決定是否套用。"));
        }
        if (!verified) return (Pick("截图寻色已结束", "截圖尋色已結束"), Pick("当前色码未完成复核，请以游戏内显示为准。", "目前色碼未完成複核，請以遊戲內顯示為準。"));
        if (accepted)
            return (Pick("截图寻色目标已达标", "截圖尋色目標已達標"), result.BestCurrent == false
                ? Pick("当前颜色符合目标要求，但未回到此前保留的配色；请以游戏当前颜色为准。", "目前顏色符合目標要求，但未回到此前保留的配色；請以遊戲目前顏色為準。")
                : Pick("游戏实际色码已复核，请在游戏内手动确认是否套用。", "遊戲實際色碼已複核，請在遊戲內手動確認是否套用。"));
        return result.BestCurrent == false
            ? (Pick("截图寻色 · 当前妥协结果", "截圖尋色 · 目前妥協結果"), Pick("当前颜色已经复核；此前更好的实测结果未完全恢复，请查看当前色差后再决定是否套用。", "目前顏色已經複核；此前更好的實測結果未完全恢復，請查看目前色差後再決定是否套用。"))
            : (Pick("截图寻色 · 妥协结果", "截圖尋色 · 妥協結果"), Pick("本轮未命中全部目标。妥协选色兼顾色相与饱和度，色差仍按原有 ΔE 显示；请在游戏内决定是否套用。", "本輪未命中全部目標。妥協選色兼顧色相與飽和度，色差仍按原有 ΔE 顯示；請在遊戲內決定是否套用。"));
    }

    /// <summary>精准寻色的一局结果（原 native_status.result_text）。</summary>
    private static (string Title, string Detail) Native(ColorRun run, ColorRunResult result)
    {
        var verified = result.CurrentVerified is not null;
        var accepted = result.CurrentVerified?.Accepted == true;
        switch (result.StopReason)
        {
            case "unavailable": return Unavailable(result.StopDetail);
            case "interrupted" when result.StopDetail == "focus_lost":
                return (Pick("游戏失去焦点，已停止寻色", "遊戲失去焦點，已停止尋色"), Pick("请保持游戏窗口在前台，再点「开始」。窗口尺寸本身不会触发此提示。", "請保持遊戲視窗在前景，再點「開始」。視窗尺寸本身不會觸發此提示。"));
            case "interrupted" when result.StopDetail == "window_changed":
                return (Pick("游戏窗口已移动或调整尺寸", "遊戲視窗已移動或調整尺寸"), Pick("运行期间窗口几何已变化，已停止旧坐标操作。请按当前窗口重新开始。", "執行期間視窗幾何已變化，已停止舊座標操作。請按目前視窗重新開始。"));
            case "interrupted" when result.StopDetail == "activation_failed":
                return (Pick("请先切回游戏窗口", "請先切回遊戲視窗"), Pick("未能将游戏切到前台，本轮已停止。请点击游戏窗口后再点「开始」。", "未能將遊戲切到前景，本輪已停止。請點擊遊戲視窗後再點「開始」。"));
            case "palette_ended":
                return (Pick("染色界面已关闭或结束", "染色介面已關閉或結束"), Pick("已停止自动操作。上次实测记录仅供查看，请以游戏当前颜色为准。", "已停止自動操作。上次實測記錄僅供查看，請以遊戲目前顏色為準。"));
            case "session_binding_changed" or "binding_changed":
                return (Pick("本局色板数据已变化", "本局色板資料已變化"), Pick("本局绑定不再成立，已停止自动操作。上次实测记录仅供查看，请以游戏当前颜色为准。", "本局綁定不再成立，已停止自動操作。上次實測記錄僅供查看，請以遊戲目前顏色為準。"));
            case "session_probe_unstable" or "session_observation_unstable":
                return (Pick("色板状态读取不稳定", "色板狀態讀取不穩定"), Pick("多次检查仍未确认本局色板状态，已停止操作并保存诊断记录。请以游戏当前颜色为准。", "多次檢查仍未確認本局色板狀態，已停止操作並儲存診斷記錄。請以遊戲目前顏色為準。"));
            case "retry_minimum_unconfirmed":
                return (Pick("重试缩放未完成确认", "重試縮放未完成確認"), Pick("画板缩小后的状态未能确认，已停止重试。请以游戏当前颜色为准。", "畫板縮小後的狀態未能確認，已停止重試。請以遊戲目前顏色為準。"));
            case "input_outcome_unknown":
                return (Pick("操作状态未完成确认", "操作狀態未完成確認"), Pick("未能确认鼠标操作和释放状态，已停止后续动作。请以游戏当前颜色为准。", "未能確認滑鼠操作和釋放狀態，已停止後續動作。請以遊戲目前顏色為準。"));
            case "initial_validation_timeout":
                return (Pick("游戏初始化检查超时", "遊戲初始化檢查逾時"), Pick("进程或窗口检查尚未完成。请在游戏普通界面重试，并在诊断页导出记录。", "程序或視窗檢查尚未完成。請在遊戲一般介面重試，並在診斷頁匯出記錄。"));
            case "discovery_read_failure":
                return (Pick("寻色准备未完成", "尋色準備未完成"), Pick("本次准备未能完成，已停止并保存诊断记录。请在游戏普通界面重试，无需消耗染色道具。", "本次準備未能完成，已停止並儲存診斷記錄。請在遊戲一般介面重試，無需消耗染色道具。"));
            case "initial_discovery_timeout" or "discovery_incomplete_timeout":
                return (Pick("色板扫描尚未完成", "色板掃描尚未完成"), Pick("本次扫描未能完成候选和唯一性检查，不能据此判断没有染色板。请在游戏普通界面重新开始，等提示就绪后再进入染色。", "本次掃描未能完成候選和唯一性檢查，不能據此判斷沒有染色板。請在遊戲一般介面重新開始，等提示就緒後再進入染色。"));
            case "ambiguous_active_palette_timeout":
                return (Pick("未能唯一绑定染色板", "未能唯一綁定染色板"), Pick("检测到多个活动色板候选。请确认只打开了一个染色板，并在诊断页导出记录。", "偵測到多個活動色板候選。請確認只打開了一個染色板，並在診斷頁匯出記錄。"));
            case "no_active_palette_timeout" or "no_active_session":
                return (Pick("等待染色界面超时", "等待染色介面逾時"), Pick("等待期间未找到已验证的活动染色板，本轮没有发送操作。请先在游戏内打开染色界面，再点「开始」。", "等待期間未找到已驗證的活動染色板，本輪沒有發送操作。請先在遊戲內打開染色介面，再點「開始」。"));
            case "initial_visual_timeout":
                return (Pick("本局色卡布局识别未完成", "本局色卡佈局辨識未完成"), Pick("已检测到本局染色板，但色卡或取色点未完成截图核对，本轮未发送操作。请以游戏当前画面为准。", "已偵測到本局染色板，但色卡或取色點未完成截圖核對，本輪未發送操作。請以遊戲目前畫面為準。"));
            case "user_candidate_observed" when verified:
                return (Pick("所选方案已复核", "所選方案已複核"), Pick("已保留您选择的方案。请查看各区域实测色差，在游戏内手动确认是否套用。", "已保留您選擇的方案。請查看各區域實測色差，在遊戲內手動確認是否套用。"));
            case "candidate_recovery_unconfirmed":
                return (Pick("所选方案返回未完成复核", "所選方案返回未完成複核"), Pick("切换后的恢复结果尚未确认。请以游戏当前颜色为准，详细记录已保存。", "切換後的恢復結果尚未確認。請以遊戲目前顏色為準，詳細記錄已儲存。"));
            case "no_available_regions":
                return (Pick("本局没有已启用的可用区域", "本局沒有已啟用的可用區域"), Pick("本局色板只有前两个区域，请启用区域 1 或 2 后重新开始。", "本局色板只有前兩個區域，請啟用區域 1 或 2 後重新開始。"));
            case "target_observed" when verified && accepted:
                return (result.CurrentVerified!.TargetExact ? Pick("目标 HEX 已精确匹配", "目標 HEX 已精確匹配") : Pick("目标颜色已在容差内匹配", "目標顏色已在容差內匹配"),
                    result.BestCurrent == false
                        ? Pick("当前颜色符合目标要求，但未回到此前保留的配色；请以游戏当前颜色为准。", "目前顏色符合目標要求，但未回到此前保留的配色；請以遊戲目前顏色為準。")
                        : Pick("游戏色码已复核，请在游戏内手动确认是否套用。", "遊戲色碼已複核，請在遊戲內手動確認是否套用。"));
            case "compromise_observed" when verified:
                return result.BestCurrent == false
                    ? (Pick("未命中目标 · 当前妥协结果", "未命中目標 · 目前妥協結果"), Pick("当前颜色已复核，但未恢复此前更好的实测结果，请在游戏内手动确认是否采用。", "目前顏色已複核，但未恢復此前更好的實測結果，請在遊戲內手動確認是否採用。"))
                    : (Pick("未命中目标 · 妥协方案", "未命中目標 · 妥協方案"), Pick("当前妥协配色已经复核。选色兼顾色相与饱和度，色差仍按原有 ΔE 显示；请在游戏内手动确认是否套用。", "目前妥協配色已經複核。選色兼顧色相與飽和度，色差仍按原有 ΔE 顯示；請在遊戲內手動確認是否套用。"));
            case "not_found_in_budget":
                return (Pick("本轮未找到达标方案", "本輪未找到達標方案"), Pick("有限搜索未找到符合目标的可执行方案，不能据此判断色板无解。可重新开始再试。", "有限搜尋未找到符合目標的可執行方案，不能據此判斷色板無解。可重新開始再試。"));
            case "insufficient_time":
                return (Pick("剩余时间不足，已停止", "剩餘時間不足，已停止"), Pick("未继续发送操作，请以游戏当前颜色为准。", "未繼續發送操作，請以遊戲目前顏色為準。"));
            case "insufficient_route_actions" or "insufficient_compromise_actions":
                return (Pick("剩余操作额度不足，已保留当前颜色", "剩餘操作額度不足，已保留目前顏色"), Pick("完整路线超过本轮剩余操作额度，未执行部分路线；请查看当前游戏颜色。", "完整路線超過本輪剩餘操作額度，未執行部分路線；請查看目前遊戲顏色。"));
            case "observation_failed":
                return (Pick("颜色复核暂未完成，已停止", "顏色複核暫未完成，已停止"), Pick("已保留能够读取的游戏色码；该记录未完成截图复核，请以游戏当前颜色为准。", "已保留能夠讀取的遊戲色碼；該記錄未完成截圖複核，請以遊戲目前顏色為準。"));
            case "already_active_palette":
                return (Pick("无法绑定当前染色板", "無法綁定目前染色板"), Pick("请确认只打开了一个染色板；关闭重叠窗口后重试。", "請確認只打開了一個染色板；關閉重疊視窗後重試。"));
            case "interrupted":
                return (Pick("已停止自动染色", "已停止自動染色"), Pick("请以游戏当前颜色为准。", "請以遊戲目前顏色為準。"));
            case "model_response_mismatch":
                return (Pick("实际操作响应不符，已停止", "實際操作回應不符，已停止"), Pick("已停止后续操作，请查看当前游戏颜色。", "已停止後續操作，請查看目前遊戲顏色。"));
            case "internal_error":
                return (Pick("工具内部错误，已停止", "工具內部錯誤，已停止"), Pick("本轮未继续发送操作，详细异常已写入诊断记录。", "本輪未繼續發送操作，詳細異常已寫入診斷記錄。"));
        }
        if (!verified) return (Pick("本轮寻色已停止", "本輪尋色已停止"), Join(Pick("当前色码未完成复核，请以游戏内显示为准。", "目前色碼未完成複核，請以遊戲內顯示為準。"),
            result.StopReason is { } other ? Pick($"原因码 {other}。", $"原因碼 {other}。") : string.Empty));
        return (Pick("本轮寻色已结束", "本輪尋色已結束"), Pick("当前颜色未达到全部目标，请以游戏内显示为准。", "目前顏色未達到全部目標，請以遊戲內顯示為準。"));
    }

    /// <summary>精准寻色准备失败的细分原因（原 native_status 的 unavailable 分支，加上宿主端口的原因码）。</summary>
    internal static (string Title, string Detail) Unavailable(string? reason) => reason switch
    {
        "process_access" => (Pick("无法读取游戏进程（权限被拒绝）", "無法讀取遊戲程序（權限被拒絕）"),
            Pick("请让 Moongate 与游戏使用相同权限级别：游戏以管理员身份运行时，关闭 Moongate 后右键选择「以管理员身份运行」再开。先在游戏普通界面重试，确认权限检查通过后再开始新一局染色。",
                "請讓 Moongate 與遊戲使用相同權限等級：遊戲以系統管理員身分執行時，關閉 Moongate 後右鍵選擇「以系統管理員身分執行」再開。先在遊戲一般介面重試，確認權限檢查通過後再開始新一局染色。")),
        "permission.missing" => (Pick("精准读取未获授权", "精準讀取未獲授權"),
            Pick("当前后端没有开放精准读取；可改用截图寻色。", "目前後端沒有開放精準讀取；可改用截圖尋色。")),
        "module_resolution" => (Pick("未能定位游戏模块", "未能定位遊戲模組"),
            Pick("已记录目标进程及实际安装路径；请在诊断页导出记录，并确认绑定的是台服游戏窗口。", "已記錄目標程序及實際安裝路徑；請在診斷頁匯出記錄，並確認綁定的是台服遊戲視窗。")),
        "game_build" or "gameBuild.incompatible" => (Pick("当前游戏版本尚未支持", "目前遊戲版本尚未支援"),
            Pick("游戏模块或数据版本与当前适配版本不同，精准寻色暂不可用；可改用截图寻色，详情已写入诊断记录。", "遊戲模組或資料版本與目前適配版本不同，精準尋色暫不可用；可改用截圖尋色，詳情已寫入診斷記錄。")),
        "coordinate_mapping" => (Pick("窗口坐标映射未完成", "視窗座標映射未完成"),
            Pick("已记录游戏内部尺寸、物理窗口尺寸及缩放信息。请把游戏窗口恢复为普通窗口后重试，或改用截图寻色。", "已記錄遊戲內部尺寸、實體視窗尺寸及縮放資訊。請把遊戲視窗還原為一般視窗後重試，或改用截圖尋色。")),
        "window_measurement" => (Pick("未能读取物理窗口坐标", "未能讀取實體視窗座標"),
            Pick("请恢复游戏窗口（不要最小化或拖到屏幕外）后重试，详情已写入诊断记录。", "請還原遊戲視窗（不要最小化或拖到螢幕外）後重試，詳情已寫入診斷記錄。")),
        "ocr_assets" => (Pick("文字识别组件不可用", "文字辨識元件不可用"),
            Pick("染色组件里的 Tesseract 文字识别文件不完整；请重新解压完整的 Moongate 压缩包。", "染色元件裡的 Tesseract 文字辨識檔案不完整；請重新解壓完整的 Moongate 壓縮檔。")),
        "server_selection" => (Pick("服务器与游戏窗口不匹配", "伺服器與遊戲視窗不匹配"),
            Pick("Moongate 只适配台服客户端；请确认左上角绑定的是台服游戏窗口。", "Moongate 只適配台服用戶端；請確認左上角綁定的是台服遊戲視窗。")),
        "gameBinding.invalid" or "input.bindingChanged" => (Pick("游戏窗口绑定已变化", "遊戲視窗綁定已變化"),
            Pick("精准读取与当前绑定的游戏窗口不符；请在左上角重新选择游戏窗口后再开始。", "精準讀取與目前綁定的遊戲視窗不符；請在左上角重新選擇遊戲視窗後再開始。")),
        _ => (Pick("当前游戏或窗口条件不支持", "目前遊戲或視窗條件不支援"),
            Pick($"请检查游戏版本、窗口和程序权限，详情已写入诊断记录（原因码 {reason ?? "preparation"}）。", $"請檢查遊戲版本、視窗和程式權限，詳情已寫入診斷記錄（原因碼 {reason ?? "preparation"}）。"))
    };

    /// <summary>精准寻色失败时停在的步骤：准备步骤与运行阶段，未登记的原样显示。</summary>
    internal static string StageName(string stage) => stage switch
    {
        "ocr_assets" => Pick("加载文字识别组件", "載入文字辨識元件"),
        "game_build" => Pick("核对游戏版本", "核對遊戲版本"),
        "window_context" => Pick("核对窗口与坐标", "核對視窗與座標"),
        "screenshot_layout" => Pick("核对截图布局", "核對截圖佈局"),
        "input_backend" => Pick("核对输入方式", "核對輸入方式"),
        "process_access" => Pick("读取游戏进程", "讀取遊戲程序"),
        _ => ColorStages.Name(stage)
    };

    // ---- 引擎说明：简体原文与繁体对照 ----

    private static readonly Dictionary<string, string> Traditional = new(StringComparer.Ordinal)
    {
        ["剩余时间用于当前画板搜索，保留回到较好结果的时间。"] = "剩餘時間用於目前畫板搜尋，保留回到較好結果的時間。",
        ["单区域寻色：先检查当前画面，找到目标后立即定位。"] = "單區域尋色：先檢查目前畫面，找到目標後立即定位。",
        ["单区域寻色：正在定位并核对游戏色码。"] = "單區域尋色：正在定位並核對遊戲色碼。",
        ["单区域寻色：正在探索新位置，每次移动后立即检查目标。"] = "單區域尋色：正在探索新位置，每次移動後立即檢查目標。",
        ["复用本局已采集的颜色，正在重新搜索。"] = "複用本局已採集的顏色，正在重新搜尋。",
        ["复用本局已采集的颜色，保留回到较好结果的时间。"] = "複用本局已採集的顏色，保留回到較好結果的時間。",
        ["实际落点与方案不符，正在校正并继续寻找较好的方案。"] = "實際落點與方案不符，正在校正並繼續尋找較好的方案。",
        ["寻色已就绪，请进入染色界面；工具正在等待活动色板。F9 可停止。"] = "尋色已就緒，請進入染色介面；工具正在等待活動色板。F9 可停止。",
        ["已找到目标色板位置，正在定位并复核。"] = "已找到目標色板位置，正在定位並複核。",
        ["已检测到本局色板，正在重新识别色卡与取色点；请保持染色画面可见。F9 可停止。"] = "已偵測到本局色板，正在重新辨識色卡與取色點；請保持染色畫面可見。F9 可停止。",
        ["已检测到活动色板，正在完成本局识别；请保持游戏窗口前台。F9 可停止。"] = "已偵測到活動色板，正在完成本局辨識；請保持遊戲視窗在前景。F9 可停止。",
        ["截图寻色已就绪，可进入染色，也可直接识别已打开的色板。F9 可停止。"] = "截圖尋色已就緒，可進入染色，也可直接辨識已打開的色板。F9 可停止。",
        ["未找到精准方案，正在定位并复核色差较小的妥协方案。"] = "未找到精準方案，正在定位並複核色差較小的妥協方案。",
        ["未能测得完整重复周期，正在使用当前截图寻找较接近的颜色。"] = "未能測得完整重複週期，正在使用目前截圖尋找較接近的顏色。",
        ["正在微调颜色，并保留已经实测的较好结果。"] = "正在微調顏色，並保留已經實測的較好結果。",
        ["正在恢复已验证的颜色。"] = "正在恢復已驗證的顏色。",
        ["正在放大采集细小颜色点，采集完成后会自动移动。"] = "正在放大採集細小顏色點，採集完成後會自動移動。",
        ["正在检查当前游戏与取色数据。"] = "正在檢查目前遊戲與取色資料。",
        ["正在细化颜色，已预留恢复时间。"] = "正在細化顏色，已預留恢復時間。",
        ["正在继续校正颜色，保留本轮已测得的较好结果。"] = "正在繼續校正顏色，保留本輪已測得的較好結果。",
        ["正在缩小画板并采集原始颜色，随后会自动寻找目标。"] = "正在縮小畫板並採集原始顏色，隨後會自動尋找目標。",
        ["正在覆盖重复色板，保留原始颜色点。"] = "正在覆蓋重複色板，保留原始顏色點。",
        ["正在计算可执行的目标方案。"] = "正在計算可執行的目標方案。",
        ["正在计算平移、旋转与缩放方案；最终颜色以游戏实际色码为准。"] = "正在計算平移、旋轉與縮放方案；最終顏色以遊戲實際色碼為準。",
        ["等待游戏色板显示完整；可以先进入染色，再启动截图寻色。F9 可停止。"] = "等待遊戲色板顯示完整；可以先進入染色，再啟動截圖尋色。F9 可停止。",
        ["正在准备寻色，请保持游戏窗口可见。F9 可停止。"] = "正在準備尋色，請保持遊戲視窗可見。F9 可停止。",
        ["正在调整色板。"] = "正在調整色板。",
        ["正在核对当前游戏色码。"] = "正在核對目前遊戲色碼。"
    };

    /// <summary>引擎说明：繁体界面按对照表换写，未登记的原样显示。</summary>
    internal static string Message(string message) => Pick(message, Traditional.GetValueOrDefault(message) ?? message);

    /// <summary>登记了繁体对照的引擎说明（测试用来核对引擎里的全部说明都已登记）。</summary>
    internal static IReadOnlyCollection<string> KnownMessages => Traditional.Keys;

    private static string Lines(IReadOnlyList<ColorRegion> regions, ColorRunReading? reading, string header)
    {
        if (reading is null || reading.HexByRegion.Count == 0) return string.Empty;
        var lines = new List<string> { header };
        for (var i = 0; i < regions.Count; i++)
        {
            if (!reading.HexByRegion.TryGetValue(regions[i].Id, out var hex)) continue;
            var delta = reading.DeltaByRegion.GetValueOrDefault(regions[i].Id);
            lines.Add(Pick($"区域 {i + 1}  ", $"區域 {i + 1}  ") + (hex ?? "—") +
                (delta is { } value ? " · " + ColorStages.Delta(value) : regions[i].Enabled ? string.Empty : " · " + Pick("未参与匹配", "未參與匹配")));
        }
        return string.Join('\n', lines);
    }

    private static string Join(params string[] parts) => string.Join('\n', parts.Where(part => part.Length > 0));
}
