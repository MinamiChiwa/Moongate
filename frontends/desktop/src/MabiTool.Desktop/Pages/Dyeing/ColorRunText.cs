using System.Globalization;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 运行栏状态行与说明行：标题是当前阶段或结果，说明是读数与一句“该做什么”，原因与背景放在 <see cref="Tip"/>（悬停提示）。
/// <see cref="Guide"/> 是去掉区域读数行的说明，游戏浮窗的染色元件另有逐区域读数，用它避免重复。
/// </summary>
public sealed record ColorRunStatus(string Title, string Detail, ColorTone Tone)
{
    public string Guide { get; init; } = Detail;

    /// <summary>悬停提示：原因、背景与技术细节；没有时为空。</summary>
    public string Tip { get; init; } = string.Empty;
}

/// <summary>
/// 寻色状态的中文呈现，按原工具 Color Studio 0.5.0 的 ui_progress.progress_text、native_status 与 screenshot_presentation：
/// 进行中按阶段、引擎说明、细化状态与重试状态给出标题与说明；结束后按宿主停止原因、引擎停止原因与细分原因给出结论和用户可做的事。
/// 文字按长度规则：标题一句话说清现在怎样，说明只写该做什么，原因与背景进悬停提示。
/// 引擎说明是简体原文，界面显示对照表里的短句，原文（繁体界面按对照表换写）进悬停提示；未登记的阶段与原因码显示“未知阶段／未知原因”并记入诊断页。
/// </summary>
public static class ColorRunText
{
    private sealed record Text(string Title, string Action, string Tip = "");

    private static Text T(string titleHans, string titleHant, string actionHans, string actionHant, string tipHans = "", string tipHant = "") =>
        new(Pick(titleHans, titleHant), Pick(actionHans, actionHant), Pick(tipHans, tipHant));

    private static readonly string StopHint = "F9 可停止";
    private static string CurrentColor => Pick("以游戏当前颜色为准", "以遊戲目前顏色為準");
    private static string Restart => Pick("确认后重新开始", "確認後重新開始");

    /// <summary>运行已结束的阶段（宿主写入的终态名）。</summary>
    public static bool IsActive(ColorRun run) => run.Phase is not ("succeeded" or "failed" or "cancelled" or "interrupted");

    public static ColorRunStatus Describe(ColorRun run)
    {
        if (IsActive(run)) return Running(run, true) with { Guide = Running(run, false).Detail };
        var ended = Ended(run);
        var tone = run.Phase is "failed" or "interrupted" ? ColorTone.Warn : ColorTone.Idle;
        return new(ended.Title, ended.Action, tone) { Tip = ended.Tip };
    }

    // ---- 进行中 ----

    private static ColorRunStatus Running(ColorRun run, bool readings)
    {
        if (run.Phase == "stopping")
            return new(Pick("正在停止", "正在停止"), CurrentColor, ColorTone.Live) { Tip = Pick("正在停止输入并收尾。", "正在停止輸入並收尾。") };
        if (run.Progress is not { } progress)
            return new(Pick("正在启动寻色", "正在啟動尋色"), StopHint, ColorTone.Live)
                { Tip = Pick("正在把游戏窗口切到前台并启动染色引擎，首次启动需要几秒。", "正在把遊戲視窗切到前景並啟動染色引擎，首次啟動需要幾秒。") };
        var regions = run.RulesSnapshot.Regions;
        string Lines(ColorRunReading? reading, string header) => readings ? ColorRunText.Lines(regions, reading, header) : string.Empty;
        switch (progress.Status)
        {
            case "retry-waiting":
                var retry = run.Interaction?.RetryEnabled == true
                    ? T("本轮寻色已完成", "本輪尋色已完成", "可点「重试」或「停止」", "可點「重試」或「停止」",
                        "重试在本局剩余时间内重新寻色；剩余时间不足时保留当前颜色。满意时点「停止」。", "重試在本局剩餘時間內重新尋色；剩餘時間不足時保留目前顏色。滿意時點「停止」。")
                    : RetryReason(progress.Reason, Pick("本轮寻色已完成", "本輪尋色已完成"));
                return new(retry.Title, Join(Lines(progress.Reading, Pick("当前配色 · 游戏实测", "目前配色 · 遊戲實測")), retry.Action), ColorTone.Live) { Tip = retry.Tip };
            case "retry-zooming":
                return new(Pick("正在重试", "正在重試"), StopHint, ColorTone.Live) { Tip = Pick("正在缩小画板并重新搜索。", "正在縮小畫板並重新搜尋。") };
            case "retry-searching":
                return new(Pick("重新搜索目标颜色", "重新搜尋目標顏色"), StopHint, ColorTone.Live) { Tip = Pick("沿用本局剩余时间重新寻色。", "沿用本局剩餘時間重新尋色。") };
            case "retry-rejected":
                var rejected = RetryReason(progress.Reason, Pick("暂时无法重试", "暫時無法重試"));
                return new(rejected.Title, rejected.Action, ColorTone.Live) { Tip = rejected.Tip };
        }
        if (progress.Stage == "refinement") return Refinement(regions, progress, readings);
        var title = ColorStages.Name(progress.Stage);
        if (progress.Total is > 0 and var total)
            title += "  " + (progress.Current ?? 0).ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
        var (body, tip) = progress.Message is { } message ? Message(message) : DefaultBody(progress.Stage);
        // 等待色卡核验时带未通过原因：说明行换成该原因的做法，原因进悬停提示。
        if (progress.Status is null && Verification(progress.Reason) is { } waiting)
            (body, tip) = (waiting.Action, JoinTip(waiting.Title + Pick("：", "：") + waiting.Tip, tip));
        return new(title, Join(Lines(progress.Reading, Pick("当前颜色 · 游戏读数", "目前顏色 · 遊戲讀數")), body), ColorTone.Live) { Tip = tip };
    }

    private static ColorRunStatus Refinement(IReadOnlyList<ColorRegion> regions, ColorRunProgress progress, bool readings)
    {
        var status = progress.Status;
        var trying = status is "trial" or "restoring" or "recovering";
        var current = Lines(regions, progress.Reading, status == "trial" ? Pick("当前试探 · 游戏实测", "目前試探 · 遊戲實測") : Pick("当前配色 · 游戏实测", "目前配色 · 遊戲實測"));
        // 浮窗元件逐区域显示当前读数，这里只保留“已保留配色”（与当前读数不同的一组颜色）。
        if (!readings) current = string.Empty;
        var retained = trying ? Lines(regions, progress.Retained, Pick("已保留配色 · 游戏实测", "已保留配色 · 遊戲實測")) : string.Empty;
        var tip = status switch
        {
            "trial" => Pick("正在核对微调结果；尚未替换已保留配色。", "正在核對微調結果；尚未替換已保留配色。"),
            "restoring" => Pick("本次微调没有改善，正在尝试返回已保留配色。", "本次微調沒有改善，正在嘗試返回已保留配色。"),
            "recovering" => Pick("返回出现偏差，正在重新定位并修正配色。", "返回出現偏差，正在重新定位並修正配色。"),
            "retained" => Pick("微调已改善，已保留本次实测配色。", "微調已改善，已保留本次實測配色。"),
            _ => Pick("正在计算更好的配色，计算期间色板可能暂时不动。", "正在計算更好的配色，計算期間色板可能暫時不動。")
        };
        var action = trying ? Pick("F9 会立即停止，以游戏颜色为准", "F9 會立即停止，以遊戲顏色為準")
            : progress.Reading is not null || retained.Length > 0 ? Pick("满意时按 F9 或点「停止」", "滿意時按 F9 或點「停止」")
            : StopHint;
        return new(Pick("正在优化当前配色", "正在最佳化目前配色"), Join(current, retained, action), ColorTone.Live) { Tip = tip };
    }

    /// <summary>引擎没有给出说明时各阶段的默认说明（原 progress_text 的缺省文字）：界面短句与悬停全文。</summary>
    internal static (string Body, string Tip) DefaultBody(string? stage) => stage switch
    {
        "discovery" => (Pick("保持游戏窗口可见", "保持遊戲視窗可見"), Pick("正在准备寻色。F9 可停止。", "正在準備尋色。F9 可停止。")),
        "waiting" => (Pick("请在游戏内进入染色", "請在遊戲內進入染色"), Pick("已经打开色板时无需重新进入。F9 可停止。", "已經打開色板時無需重新進入。F9 可停止。")),
        "align" or "period" or "stitch" or "export" or "validate" or "search" or "similarity" or "ready" =>
            (StopHint, Pick("正在计算目标位置，色板暂时不动。", "正在計算目標位置，色板暫時不動。")),
        _ => (Pick("保持游戏在前台", "保持遊戲在前景"), Pick("正在调整色板。F9 可停止。", "正在調整色板。F9 可停止。"))
    };

    /// <summary>重试不可用或被拒绝的原因（原 native_status.retry_reason_text）：标题写原因，说明写已保留当前颜色。</summary>
    private static Text RetryReason(string? reason, string fallbackTitle)
    {
        var kept = Pick("已保留当前颜色", "已保留目前顏色");
        return reason switch
        {
            "insufficient_retry_time" => new(Pick("剩余时间不够重试", "剩餘時間不夠重試"), kept),
            "insufficient_retry_actions" => new(Pick("剩余操作次数不够重试", "剩餘操作次數不夠重試"), kept),
            "action_limit" => new(Pick("自动操作次数已达上限", "自動操作次數已達上限"), kept),
            "round_limit" => new(Pick("自动搜索次数已达上限", "自動搜尋次數已達上限"), kept),
            "unsupported_retry_settings" => new(Pick("无法确认缩小路线", "無法確認縮小路線"), kept, Pick("无法确认当前画板的缩小路线。", "無法確認目前畫板的縮小路線。")),
            "retry_recovery_unavailable" => new(Pick("无法确认恢复路线", "無法確認恢復路線"), kept, Pick("无法确认重试后的恢复路线。", "無法確認重試後的恢復路線。")),
            "retry_resetting" => new(Pick("当前动作尚未结束", "目前動作尚未結束"), Pick("稍候再重试", "稍候再重試")),
            "session_closing" => new(Pick("本局正在结束", "本局正在結束"), kept),
            null or "" => new(fallbackTitle, kept),
            var other => new(fallbackTitle, kept, Localization.CodeText.Unknown("color-retry", other, "未知原因", "未知原因"))
        };
    }

    // ---- 结束 ----

    private static Text Ended(ColorRun run)
    {
        var result = run.Result;
        if (run.Phase == "cancelled" && run.StopReason is not ("interrupted" or "palette_ended"))
            return run.StopReason switch
            {
                "frontend-lost" => T("界面失联，寻色已停止", "介面失聯，尋色已停止", "以游戏当前颜色为准", "以遊戲目前顏色為準",
                    "后端超过 5 秒没有收到 Moongate 界面的续约，已停止游戏输入。", "後端超過 5 秒沒有收到 Moongate 介面的續約，已停止遊戲輸入。"),
                "emergency-stop" => T("已紧急停止", "已緊急停止", "以游戏当前颜色为准", "以遊戲目前顏色為準",
                    "已停止全部游戏输入。", "已停止全部遊戲輸入。"),
                "host-stopped" => T("后端已停止，寻色结束", "後端已停止，尋色結束", "以游戏当前颜色为准", "以遊戲目前顏色為準",
                    "后端关闭或重启时停止了寻色。", "後端關閉或重啟時停止了尋色。"),
                _ => T("已停止寻色", "已停止尋色", "在游戏内决定是否套用", "在遊戲內決定是否套用",
                    "自动操作已停止；请以游戏当前颜色为准。", "自動操作已停止；請以遊戲目前顏色為準。")
            };
        // 一局开始前就结束：宿主或 Worker 只给出错误码。
        if (result is null || run.StopReason is { } code && code.Contains('.'))
            return Code(run.StopReason, run.Phase);
        var text = run.RulesSnapshot.Strategy.KnownValue == ColorStrategy.Precise ? Native(result) : Screenshot(result);
        if (result.FailedStage is { } stage && run.Phase is "failed" or "interrupted")
            text = text with { Tip = JoinTip(text.Tip, Pick("停在：", "停在：") + StageName(stage)) };
        return text;
    }

    /// <summary>宿主与 Worker 的错误码（一局开始前结束）。</summary>
    private static Text Code(string? code, string phase) => code switch
    {
        "input.focusChanged" => T("游戏失去焦点，已停止", "遊戲失去焦點，已停止", "点一下游戏窗口再开始", "點一下遊戲視窗再開始",
            "寻色期间游戏窗口必须保持在前台。", "尋色期間遊戲視窗必須保持在前景。"),
        "input.bindingChanged" => T("游戏窗口已移动或缩放", "遊戲視窗已移動或縮放", "确认窗口绑定后重新开始", "確認視窗綁定後重新開始",
            "运行期间窗口位置或尺寸发生变化，已停止按旧坐标操作。", "執行期間視窗位置或尺寸發生變化，已停止按舊座標操作。"),
        "input.deadlineExceeded" => T("寻色时限已到", "尋色時限已到", "以游戏当前颜色为准", "以遊戲目前顏色為準",
            "本局操作时间已用完，已停止输入。", "本局操作時間已用完，已停止輸入。"),
        "input.runStopped" => T("已停止寻色", "已停止尋色", "以游戏当前颜色为准", "以遊戲目前顏色為準"),
        "resource.busy" => T("游戏输入被占用", "遊戲輸入被佔用", "其他操作结束后再开始", "其他操作結束後再開始",
            "合奏或其他操作正在使用游戏输入。", "合奏或其他操作正在使用遊戲輸入。"),
        "capture.sourceFailed" or "capture.frameInvalid" => T("截取游戏画面失败", "擷取遊戲畫面失敗", "还原游戏窗口后重新开始", "還原遊戲視窗後重新開始",
            "无法截取游戏客户区；确认游戏窗口没有最小化。", "無法擷取遊戲用戶區；確認遊戲視窗沒有最小化。"),
        "permission.missing" => Unavailable("permission.missing"),
        "gameBuild.incompatible" => Unavailable("game_build"),
        "color.mediaFeaturesMissing" => T("缺少 Windows 媒体功能包", "缺少 Windows 媒體功能套件", "安装媒体功能包后重开", "安裝媒體功能套件後重開",
            "Windows N 版需在可选功能中安装媒体功能包。", "Windows N 版需在選用功能中安裝媒體功能套件。"),
        "operation.executionFailed" => T("染色引擎运行出错", "染色引擎執行出錯", "请重新开始", "請重新開始",
            "已停止输入；详细记录已写入诊断（诊断页可导出）。", "已停止輸入；詳細記錄已寫入診斷（診斷頁可匯出）。"),
        "provider.unavailable" or "provider.unconfigured" or "component.missing" => T("染色组件不可用", "染色元件不可用", "重新解压完整压缩包", "重新解壓完整壓縮檔",
            "染色组件缺失或不完整；请重新解压完整的 Moongate 压缩包后再开始。", "染色元件缺失或不完整；請重新解壓完整的 Moongate 壓縮檔後再開始。"),
        "worker.startFailed" or "worker.startTimeout" or "worker.handshakeInvalid" or "worker.protocolIncompatible" or "worker.unconfigured" or "worker.platformUnsupported" =>
            T("染色引擎没有启动", "染色引擎沒有啟動", "重新解压完整压缩包", "重新解壓完整壓縮檔",
                "染色引擎未能启动或与当前版本不符；详细记录已写入诊断。", "染色引擎未能啟動或與目前版本不符；詳細記錄已寫入診斷。"),
        { } worker when worker.StartsWith("worker.", StringComparison.Ordinal) =>
            new(Pick("与染色引擎的连接中断", "與染色引擎的連線中斷"), Pick("以游戏当前颜色为准，再重新开始", "以遊戲目前顏色為準，再重新開始"),
                Localization.CodeText.Reason(worker) + Pick("，已停止输入。", "，已停止輸入。")),
        _ when phase == "interrupted" => new(Pick("染色引擎中断", "染色引擎中斷"), Pick("以游戏当前颜色为准，再重新开始", "以遊戲目前顏色為準，再重新開始"),
            Localization.CodeText.Reason(code) + Pick("，已停止输入。", "，已停止輸入。")),
        _ => new(Pick("染色引擎未完成运行", "染色引擎未完成執行"), Pick("请重新开始", "請重新開始"),
            Localization.CodeText.Reason(code) + Pick("；详细记录已写入诊断。", "；詳細記錄已寫入診斷。"))
    };

    /// <summary>截图寻色的一局结果（原 screenshot_presentation.result_text）。</summary>
    private static Text Screenshot(ColorRunResult result)
    {
        var verified = result.CurrentVerified is not null;
        var accepted = result.CurrentVerified?.Accepted == true;
        var apply = Pick("在游戏内决定是否套用", "在遊戲內決定是否套用");
        switch (result.StopReason)
        {
            case "interrupted" when result.StopDetail is "focus_lost" or "window_changed" or "activation_failed":
                return Native(result);
            case "interrupted" when verified:
                return T("截图寻色已停止", "截圖尋色已停止", "在游戏内决定是否套用", "在遊戲內決定是否套用", "游戏实际色码已复核。", "遊戲實際色碼已複核。");
            case "palette_ended" or "interrupted":
                return new(Pick("截图寻色已停止", "截圖尋色已停止"), CurrentColor,
                    result.StopReason == "palette_ended" ? Pick("染色界面已关闭或本局已结束。", "染色介面已關閉或本局已結束。") : string.Empty);
            case "processing_error":
                return new(Pick("截图寻色处理异常", "截圖尋色處理異常"), !verified || result.BestCurrent == false ? CurrentColor : apply,
                    !verified ? Pick("错误记录已保存，当前色码未完成复核。", "錯誤記錄已儲存，目前色碼未完成複核。")
                    : result.BestCurrent == false ? Pick("错误记录已保存；当前颜色已复核，但未能恢复此前较好的结果。", "錯誤記錄已儲存；目前顏色已複核，但未能恢復此前較好的結果。")
                    : Pick("错误记录已保存；已保留或恢复本轮较好的实测颜色。", "錯誤記錄已儲存；已保留或恢復本輪較好的實測顏色。"));
        }
        if (!verified) return T("截图寻色已结束", "截圖尋色已結束", "以游戏内显示为准", "以遊戲內顯示為準", "当前色码未完成复核。", "目前色碼未完成複核。");
        if (accepted)
            return result.BestCurrent == false
                ? T("截图寻色目标已达标", "截圖尋色目標已達標", "以游戏当前颜色为准", "以遊戲目前顏色為準", "当前颜色符合目标，但未回到此前保留的配色。", "目前顏色符合目標，但未回到此前保留的配色。")
                : T("截图寻色目标已达标", "截圖尋色目標已達標", "在游戏内决定是否套用", "在遊戲內決定是否套用", "游戏实际色码已复核。", "遊戲實際色碼已複核。");
        return result.BestCurrent == false
            ? T("截图寻色 · 当前妥协结果", "截圖尋色 · 目前妥協結果", "查看色差后决定是否套用", "查看色差後決定是否套用",
                "当前颜色已复核；此前更好的实测结果未完全恢复。", "目前顏色已複核；此前更好的實測結果未完全恢復。")
            : T("截图寻色 · 妥协结果", "截圖尋色 · 妥協結果", "在游戏内决定是否套用", "在遊戲內決定是否套用",
                "本轮未命中全部目标。妥协选色兼顾色相与饱和度，色差仍按原有 ΔE 显示。", "本輪未命中全部目標。妥協選色兼顧色相與飽和度，色差仍按原有 ΔE 顯示。");
    }

    /// <summary>精准寻色的一局结果（原 native_status.result_text）。</summary>
    private static Text Native(ColorRunResult result)
    {
        var verified = result.CurrentVerified is not null;
        var accepted = result.CurrentVerified?.Accepted == true;
        switch (result.StopReason)
        {
            case "unavailable": return Unavailable(result.StopDetail);
            case "interrupted" when result.StopDetail == "focus_lost":
                return T("游戏失去焦点，已停止", "遊戲失去焦點，已停止", "点一下游戏窗口再开始", "點一下遊戲視窗再開始",
                    "寻色期间游戏窗口必须保持在前台；窗口尺寸本身不会触发此提示。", "尋色期間遊戲視窗必須保持在前景；視窗尺寸本身不會觸發此提示。");
            case "interrupted" when result.StopDetail == "window_changed":
                return T("游戏窗口已移动或缩放", "遊戲視窗已移動或縮放", "按当前窗口重新开始", "按目前視窗重新開始",
                    "运行期间窗口几何已变化，已停止旧坐标操作。", "執行期間視窗幾何已變化，已停止舊座標操作。");
            case "interrupted" when result.StopDetail == "activation_failed":
                return T("没能切到游戏窗口", "沒能切到遊戲視窗", "点一下游戏窗口再开始", "點一下遊戲視窗再開始", "未能将游戏切到前台，本轮已停止。", "未能將遊戲切到前景，本輪已停止。");
            case "palette_ended":
                return T("染色界面已关闭", "染色介面已關閉", "以游戏当前颜色为准", "以遊戲目前顏色為準",
                    "已停止自动操作；上次实测记录仅供查看。", "已停止自動操作；上次實測記錄僅供查看。");
            case "session_binding_changed" or "binding_changed":
                return T("本局色板数据已变化", "本局色板資料已變化", "以游戏当前颜色为准", "以遊戲目前顏色為準",
                    "本局绑定不再成立，已停止自动操作；上次实测记录仅供查看。", "本局綁定不再成立，已停止自動操作；上次實測記錄僅供查看。");
            case "session_probe_unstable" or "session_observation_unstable":
                return T("色板状态读取不稳定", "色板狀態讀取不穩定", "以游戏当前颜色为准", "以遊戲目前顏色為準",
                    "多次检查仍未确认本局色板状态，已停止操作并保存诊断记录。", "多次檢查仍未確認本局色板狀態，已停止操作並儲存診斷記錄。");
            case "retry_minimum_unconfirmed":
                return T("重试缩放未确认", "重試縮放未確認", "以游戏当前颜色为准", "以遊戲目前顏色為準", "画板缩小后的状态未能确认，已停止重试。", "畫板縮小後的狀態未能確認，已停止重試。");
            case "input_outcome_unknown":
                return T("操作结果未确认", "操作結果未確認", "以游戏当前颜色为准", "以遊戲目前顏色為準", "未能确认鼠标操作和释放状态，已停止后续动作。", "未能確認滑鼠操作和釋放狀態，已停止後續動作。");
            case "initial_validation_timeout":
                return T("游戏初始化检查超时", "遊戲初始化檢查逾時", "在游戏普通界面重试", "在遊戲一般介面重試", "进程或窗口检查尚未完成；可在诊断页导出记录。", "程序或視窗檢查尚未完成；可在診斷頁匯出記錄。");
            case "discovery_read_failure":
                return T("寻色准备未完成", "尋色準備未完成", "在游戏普通界面重试", "在遊戲一般介面重試", "已停止并保存诊断记录；重试无需消耗染色道具。", "已停止並儲存診斷記錄；重試無需消耗染色道具。");
            case "initial_discovery_timeout" or "discovery_incomplete_timeout":
                return T("色板扫描未完成", "色板掃描未完成", "回到普通界面重新开始", "回到一般介面重新開始",
                    "本次扫描未能完成候选和唯一性检查，不能据此判断没有染色板。等提示就绪后再进入染色。", "本次掃描未能完成候選和唯一性檢查，不能據此判斷沒有染色板。等提示就緒後再進入染色。");
            case "ambiguous_active_palette_timeout":
                return T("未能唯一绑定染色板", "未能唯一綁定染色板", "只打开一个染色板", "只打開一個染色板", "检测到多个活动色板候选；可在诊断页导出记录。", "偵測到多個活動色板候選；可在診斷頁匯出記錄。");
            case "no_active_palette_timeout" or "no_active_session":
                return T("等待染色界面超时", "等待染色介面逾時", "先打开染色界面再开始", "先打開染色介面再開始", "等待期间未找到已验证的活动染色板，本轮没有发送操作。", "等待期間未找到已驗證的活動染色板，本輪沒有發送操作。");
            case "initial_visual_timeout" when Verification(result.StopDetail) is { } failed:
                return failed;
            case "initial_visual_timeout":
                return T("色卡布局识别未完成", "色卡佈局辨識未完成", "以游戏当前画面为准", "以遊戲目前畫面為準", "已检测到本局染色板，但色卡或取色点未完成截图核对，本轮未发送操作。", "已偵測到本局染色板，但色卡或取色點未完成截圖核對，本輪未發送操作。");
            case "observation_failed" when Verification(result.StopDetail) is { } failed:
                return failed;
            case "user_candidate_observed" when verified:
                return T("所选方案已复核", "所選方案已複核", "在游戏内决定是否套用", "在遊戲內決定是否套用", "已保留您选择的方案；可查看各区域实测色差。", "已保留您選擇的方案；可查看各區域實測色差。");
            case "candidate_recovery_unconfirmed":
                return T("所选方案返回未复核", "所選方案返回未複核", "以游戏当前颜色为准", "以遊戲目前顏色為準", "切换后的恢复结果尚未确认，详细记录已保存。", "切換後的恢復結果尚未確認，詳細記錄已儲存。");
            case "no_available_regions":
                return T("本局没有可用区域", "本局沒有可用區域", "启用区域 1 或 2 后重新开始", "啟用區域 1 或 2 後重新開始", "本局色板只有前两个区域。", "本局色板只有前兩個區域。");
            case "target_observed" when verified && accepted:
                return new(result.CurrentVerified!.TargetExact ? Pick("目标 HEX 已精确匹配", "目標 HEX 已精確匹配") : Pick("目标颜色已在容差内匹配", "目標顏色已在容差內匹配"),
                    result.BestCurrent == false ? CurrentColor : Pick("在游戏内决定是否套用", "在遊戲內決定是否套用"),
                    result.BestCurrent == false ? Pick("当前颜色符合目标，但未回到此前保留的配色。", "目前顏色符合目標，但未回到此前保留的配色。") : Pick("游戏色码已复核。", "遊戲色碼已複核。"));
            case "compromise_observed" when verified:
                return result.BestCurrent == false
                    ? T("未命中 · 当前妥协结果", "未命中 · 目前妥協結果", "在游戏内决定是否采用", "在遊戲內決定是否採用", "当前颜色已复核，但未恢复此前更好的实测结果。", "目前顏色已複核，但未恢復此前更好的實測結果。")
                    : T("未命中目标 · 妥协方案", "未命中目標 · 妥協方案", "在游戏内决定是否套用", "在遊戲內決定是否套用",
                        "当前妥协配色已经复核。选色兼顾色相与饱和度，色差仍按原有 ΔE 显示。", "目前妥協配色已經複核。選色兼顧色相與飽和度，色差仍按原有 ΔE 顯示。");
            case "not_found_in_budget":
                return T("本轮未找到达标方案", "本輪未找到達標方案", "可重新开始再试", "可重新開始再試", "有限搜索未找到符合目标的可执行方案，不能据此判断色板无解。", "有限搜尋未找到符合目標的可執行方案，不能據此判斷色板無解。");
            case "insufficient_time":
                return T("剩余时间不足，已停止", "剩餘時間不足，已停止", "以游戏当前颜色为准", "以遊戲目前顏色為準", "未继续发送操作。", "未繼續發送操作。");
            case "insufficient_route_actions" or "insufficient_compromise_actions":
                return T("操作额度不足，已保留", "操作額度不足，已保留", "查看当前游戏颜色", "查看目前遊戲顏色", "完整路线超过本轮剩余操作额度，未执行部分路线。", "完整路線超過本輪剩餘操作額度，未執行部分路線。");
            case "observation_failed":
                return T("颜色复核未完成", "顏色複核未完成", "以游戏当前颜色为准", "以遊戲目前顏色為準", "已保留能够读取的游戏色码；该记录未完成截图复核。", "已保留能夠讀取的遊戲色碼；該記錄未完成截圖複核。");
            case "already_active_palette":
                return T("无法绑定当前染色板", "無法綁定目前染色板", "关闭重叠窗口后重试", "關閉重疊視窗後重試", "请确认只打开了一个染色板。", "請確認只打開了一個染色板。");
            case "interrupted":
                return T("已停止自动染色", "已停止自動染色", "以游戏当前颜色为准", "以遊戲目前顏色為準");
            case "model_response_mismatch":
                return T("操作响应不符，已停止", "操作回應不符，已停止", "查看当前游戏颜色", "查看目前遊戲顏色", "实际操作响应与模型不符，已停止后续操作。", "實際操作回應與模型不符，已停止後續操作。");
            case "internal_error":
                return T("工具内部错误，已停止", "工具內部錯誤，已停止", "以游戏当前颜色为准", "以遊戲目前顏色為準", "本轮未继续发送操作，详细异常已写入诊断记录。", "本輪未繼續發送操作，詳細異常已寫入診斷記錄。");
        }
        if (!verified)
            return new(Pick("本轮寻色已停止", "本輪尋色已停止"), Pick("以游戏内显示为准", "以遊戲內顯示為準"),
                JoinTip(Pick("当前色码未完成复核。", "目前色碼未完成複核。"),
                    result.StopReason is { } other && !KnownEndings.Contains(other) ? Localization.CodeText.Unknown("color-stop", other, "未知原因", "未知原因") : string.Empty));
        return T("本轮寻色已结束", "本輪尋色已結束", "以游戏内显示为准", "以遊戲內顯示為準", "当前颜色未达到全部目标。", "目前顏色未達到全部目標。");
    }

    /// <summary>引擎结果里不需要额外说明的停止原因：目标与妥协的正常结束、复核与用户停止。</summary>
    private static readonly HashSet<string> KnownEndings = new(StringComparer.Ordinal)
    {
        "target_observed", "compromise_observed", "user_candidate_observed", "target_exact", "predicted_exact", "predicted_candidate",
        "time_used", "text_time_used", "recovery_time_used", "stopped", "user_stop", "completed", "not_found", "no_improvement"
    };

    /// <summary>精准寻色准备失败的细分原因（原 native_status 的 unavailable 分支，加上宿主端口的原因码）。</summary>
    private static Text Unavailable(string? reason) => reason switch
    {
        "process_access" => T("无法读取游戏进程", "無法讀取遊戲程序", "以相同权限运行 Moongate", "以相同權限執行 Moongate",
            "权限被拒绝。游戏以管理员身份运行时，关闭 Moongate 后右键选择「以管理员身份运行」再开；先在游戏普通界面重试，确认权限检查通过后再开始新一局染色。",
            "權限被拒絕。遊戲以系統管理員身分執行時，關閉 Moongate 後右鍵選擇「以系統管理員身分執行」再開；先在遊戲一般介面重試，確認權限檢查通過後再開始新一局染色。"),
        "permission.missing" => T("精准读取未获授权", "精準讀取未獲授權", "可改用截图寻色", "可改用截圖尋色", "当前后端没有开放精准读取。", "目前後端沒有開放精準讀取。"),
        "module_resolution" => T("未能定位游戏模块", "未能定位遊戲模組", "确认绑定的是台服游戏", "確認綁定的是台服遊戲", "已记录目标进程及实际安装路径；可在诊断页导出记录。", "已記錄目標程序及實際安裝路徑；可在診斷頁匯出記錄。"),
        "game_build" or "gameBuild.incompatible" => T("当前游戏版本尚未支持", "目前遊戲版本尚未支援", "可改用截图寻色", "可改用截圖尋色",
            "游戏模块或数据版本与当前适配版本不同，精准寻色暂不可用；详情已写入诊断记录。", "遊戲模組或資料版本與目前適配版本不同，精準尋色暫不可用；詳情已寫入診斷記錄。"),
        "coordinate_mapping" => T("窗口坐标映射未完成", "視窗座標映射未完成", "恢复普通窗口后重试", "還原一般視窗後重試",
            "已记录游戏内部尺寸、物理窗口尺寸及缩放信息；也可改用截图寻色。", "已記錄遊戲內部尺寸、實體視窗尺寸及縮放資訊；也可改用截圖尋色。"),
        "window_measurement" => T("未能读取窗口坐标", "未能讀取視窗座標", "还原游戏窗口后重试", "還原遊戲視窗後重試",
            "不要最小化或拖到屏幕外；详情已写入诊断记录。", "不要最小化或拖到螢幕外；詳情已寫入診斷記錄。"),
        "ocr_assets" => T("文字识别组件不可用", "文字辨識元件不可用", "重新解压完整压缩包", "重新解壓完整壓縮檔",
            "染色组件里的 Tesseract 文字识别文件不完整。", "染色元件裡的 Tesseract 文字辨識檔案不完整。"),
        "server_selection" => T("服务器与窗口不匹配", "伺服器與視窗不匹配", "确认绑定的是台服游戏", "確認綁定的是台服遊戲", "Moongate 只适配台服客户端。", "Moongate 只適配台服用戶端。"),
        "gameBinding.invalid" or "input.bindingChanged" => T("游戏窗口绑定已变化", "遊戲視窗綁定已變化", "重新选择游戏窗口", "重新選擇遊戲視窗",
            "精准读取与当前绑定的游戏窗口不符。", "精準讀取與目前綁定的遊戲視窗不符。"),
        "window_context" => T("未能核对游戏窗口", "未能核對遊戲視窗", "保持游戏窗口不动后重试", "保持遊戲視窗不動後重試",
            "核对时游戏窗口的尺寸、位置或画面状态发生变化，或同时找到多个游戏窗口。不要拖动、缩放或最小化游戏窗口；仍失败时可改用截图寻色。",
            "核對時遊戲視窗的尺寸、位置或畫面狀態發生變化，或同時找到多個遊戲視窗。不要拖動、縮放或最小化遊戲視窗；仍失敗時可改用截圖尋色。"),
        "input_backend" => T("未能核对游戏输入方式", "未能核對遊戲輸入方式", "可改用截图寻色", "可改用截圖尋色",
            "游戏的输入状态读取前后不一致（例如正在切换界面或加载）；回到普通界面后重试。", "遊戲的輸入狀態讀取前後不一致（例如正在切換介面或載入）；回到一般介面後重試。"),
        "process_changed" => T("游戏进程已变化", "遊戲程序已變化", "重新选择游戏窗口", "重新選擇遊戲視窗",
            "校验期间游戏退出或重新启动。", "校驗期間遊戲結束或重新啟動。"),
        "unity_engine" => T("游戏引擎已更新", "遊戲引擎已更新", "可改用截图寻色", "可改用截圖尋色",
            "游戏引擎已更新，精准寻色暂不可用，可改用截图寻色。", "遊戲引擎已更新，精準尋色暫不可用，可改用截圖尋色。"),
        "render_scale" => T("画面分辨率无法对应", "畫面解析度無法對應", "改为窗口化后重试", "改為視窗化後重試",
            "请改为窗口化或把分辨率设为与显示器一致。", "請改為視窗化或把解析度設為與顯示器一致。"),
        "input_assistant" => T("游戏输入被辅助接管", "遊戲輸入被輔助接管", "可改用截图寻色", "可改用截圖尋色",
            "游戏启用了输入辅助，模拟鼠标可能无效。", "遊戲啟用了輸入輔助，模擬滑鼠可能無效。"),
        "ocr.componentMissing" => T("文字识别组件缺失", "文字辨識元件缺失", "重新解压完整压缩包", "重新解壓完整壓縮檔",
            "染色组件里没有文字识别程序或数据。", "染色元件裡沒有文字辨識程式或資料。"),
        "ocr.componentInvalid" => T("文字识别组件无法启动", "文字辨識元件無法啟動", "重新解压完整压缩包", "重新解壓完整壓縮檔",
            "文字识别程序无法启动或加载数据。", "文字辨識程式無法啟動或載入資料。"),
        "ocr.pathUnreadable" => T("文字识别读不到路径", "文字辨識讀不到路徑", "移到纯英文路径的文件夹", "移到純英文路徑的資料夾",
            "程序文件夹路径含系统编码不支持的字。", "程式資料夾路徑含系統編碼不支援的字。"),
        _ when Verification(reason) is { } failed => failed,
        _ => new(Pick("精准寻色校验未通过", "精準尋色校驗未通過"), Pick("可改用截图寻色", "可改用截圖尋色"),
            JoinTip(Pick("检查游戏窗口与权限后重试；详情已写入诊断记录。", "檢查遊戲視窗與權限後重試；詳情已寫入診斷記錄。"), reason is null or "preparation" ? string.Empty
                : Localization.CodeText.Unknown("color-unavailable", reason, "未知原因", "未知原因")))
    };

    /// <summary>
    /// 截图核验未通过与观察失败的细分原因：遮挡、识别超时、未读出、色码不一致、色块偏差、布局、显示缩放。
    /// 原因码可带“.区域序号”，悬停提示写明区域；未登记的原因返回 null，由调用处用原有说明。
    /// </summary>
    private static Text? Verification(string? code)
    {
        if (code is null) return null;
        var dot = code.IndexOf('.');
        var kind = dot < 0 ? code : code[..dot];
        var region = dot > 0 && int.TryParse(code[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 3
            ? Pick($"（区域 {number}）", $"（區域 {number}）") : string.Empty;
        Text? text = kind switch
        {
            "occluded" => T("色卡被其他窗口遮挡", "色卡被其他視窗遮擋", "移开遮挡窗口后重试", "移開遮擋視窗後重試",
                "染色画面上方有窗口，本轮未操作。", "染色畫面上方有視窗，本輪未操作。"),
            "ocr_timeout" => T("色码识别超时", "色碼辨識逾時", "关闭占用大的程序后重试", "關閉佔用大的程式後重試",
                "电脑较忙，文字识别来不及完成。", "電腦較忙，文字辨識來不及完成。"),
            "ocr_unread" => T("未读出色卡色码", "未讀出色卡色碼", "保持染色画面清晰可见", "保持染色畫面清晰可見",
                "截图里的色码文字未能识别。", "截圖裡的色碼文字未能辨識。"),
            "hex_mismatch" => T("色码核对不一致", "色碼核對不一致", "关闭画面滤镜后重试", "關閉畫面濾鏡後重試",
                "截图色码与游戏数据不同，本轮未操作。", "截圖色碼與遊戲資料不同，本輪未操作。"),
            "swatch_mismatch" => T("色块颜色有偏差", "色塊顏色有偏差", "关闭滤镜或夜间模式", "關閉濾鏡或夜間模式",
                "截图色块与色码相差过大。", "截圖色塊與色碼相差過大。"),
            "layout" => T("色卡布局识别未完成", "色卡佈局辨識未完成", "保持染色画面完整可见", "保持染色畫面完整可見",
                "截图中的色卡或取色点未能识别。", "截圖中的色卡或取色點未能辨識。"),
            "display_scale" => T("显示缩放导致画面尺寸不一致", "顯示縮放導致畫面尺寸不一致", "还原游戏窗口后重试", "還原遊戲視窗後重試",
                "截图尺寸与游戏画面像素不符。", "截圖尺寸與遊戲畫面像素不符。"),
            _ => null
        };
        return text is null ? null : new(text.Title, text.Action, text.Tip + region);
    }

    /// <summary>精准寻色失败时停在的步骤：准备步骤与运行阶段。</summary>
    internal static string StageName(string stage) => stage switch
    {
        "ocr_assets" => Pick("加载文字识别组件", "載入文字辨識元件"),
        "game_build" => Pick("核对游戏版本", "核對遊戲版本"),
        "window_context" => Pick("核对窗口与坐标", "核對視窗與座標"),
        "screenshot_layout" => Pick("核对截图布局", "核對截圖佈局"),
        "input_backend" => Pick("核对输入方式", "核對輸入方式"),
        "process_access" => Pick("读取游戏进程", "讀取遊戲程序"),
        "initial" => Pick("准备寻色", "準備尋色"),
        "timer_grace_binding" => Pick("等待计时稳定", "等待計時穩定"),
        "return_protection" => Pick("返回较好结果", "返回較好結果"),
        _ => ColorStages.Name(stage)
    };

    // ---- 引擎说明：界面短句、简体原文与繁体对照 ----

    /// <summary>引擎说明（简体原文）→ 界面短句（简、繁）与繁体全文；全文进悬停提示。</summary>
    private static readonly Dictionary<string, (string ShortHans, string ShortHant, string FullHant)> Messages = new(StringComparer.Ordinal)
    {
        ["剩余时间用于当前画板搜索，保留回到较好结果的时间。"] = ("搜索当前画板", "搜尋目前畫板", "剩餘時間用於目前畫板搜尋，保留回到較好結果的時間。"),
        ["单区域寻色：先检查当前画面，找到目标后立即定位。"] = ("检查当前画面", "檢查目前畫面", "單區域尋色：先檢查目前畫面，找到目標後立即定位。"),
        ["单区域寻色：正在定位并核对游戏色码。"] = ("定位并核对色码", "定位並核對色碼", "單區域尋色：正在定位並核對遊戲色碼。"),
        ["单区域寻色：正在探索新位置，每次移动后立即检查目标。"] = ("探索新位置", "探索新位置", "單區域尋色：正在探索新位置，每次移動後立即檢查目標。"),
        ["复用本局已采集的颜色，正在重新搜索。"] = ("重新搜索中", "重新搜尋中", "複用本局已採集的顏色，正在重新搜尋。"),
        ["复用本局已采集的颜色，保留回到较好结果的时间。"] = ("复用已采集的颜色", "複用已採集的顏色", "複用本局已採集的顏色，保留回到較好結果的時間。"),
        ["实际落点与方案不符，正在校正并继续寻找较好的方案。"] = ("校正落点中", "校正落點中", "實際落點與方案不符，正在校正並繼續尋找較好的方案。"),
        ["寻色已就绪，请进入染色界面；工具正在等待活动色板。F9 可停止。"] = ("请进入染色界面", "請進入染色介面", "尋色已就緒，請進入染色介面；工具正在等待活動色板。F9 可停止。"),
        ["已找到目标色板位置，正在定位并复核。"] = ("已找到目标，正在复核", "已找到目標，正在複核", "已找到目標色板位置，正在定位並複核。"),
        ["已检测到本局色板，正在重新识别色卡与取色点；请保持染色画面可见。F9 可停止。"] = ("保持染色画面可见", "保持染色畫面可見", "已偵測到本局色板，正在重新辨識色卡與取色點；請保持染色畫面可見。F9 可停止。"),
        ["已检测到活动色板，正在完成本局识别；请保持游戏窗口前台。F9 可停止。"] = ("保持游戏在前台", "保持遊戲在前景", "已偵測到活動色板，正在完成本局辨識；請保持遊戲視窗在前景。F9 可停止。"),
        ["截图寻色已就绪，可进入染色，也可直接识别已打开的色板。F9 可停止。"] = ("请进入染色界面", "請進入染色介面", "截圖尋色已就緒，可進入染色，也可直接辨識已打開的色板。F9 可停止。"),
        ["未找到精准方案，正在定位并复核色差较小的妥协方案。"] = ("复核妥协方案中", "複核妥協方案中", "未找到精準方案，正在定位並複核色差較小的妥協方案。"),
        ["未能测得完整重复周期，正在使用当前截图寻找较接近的颜色。"] = ("寻找较接近的颜色", "尋找較接近的顏色", "未能測得完整重複週期，正在使用目前截圖尋找較接近的顏色。"),
        ["正在微调颜色，并保留已经实测的较好结果。"] = ("微调颜色中", "微調顏色中", "正在微調顏色，並保留已經實測的較好結果。"),
        ["正在恢复已验证的颜色。"] = ("恢复已验证的颜色", "恢復已驗證的顏色", "正在恢復已驗證的顏色。"),
        ["正在放大采集细小颜色点，采集完成后会自动移动。"] = ("放大采集中", "放大採集中", "正在放大採集細小顏色點，採集完成後會自動移動。"),
        ["正在检查当前游戏与取色数据。"] = ("检查游戏与取色数据", "檢查遊戲與取色資料", "正在檢查目前遊戲與取色資料。"),
        ["正在细化颜色，已预留恢复时间。"] = ("细化颜色中", "細化顏色中", "正在細化顏色，已預留恢復時間。"),
        ["正在继续校正颜色，保留本轮已测得的较好结果。"] = ("校正颜色中", "校正顏色中", "正在繼續校正顏色，保留本輪已測得的較好結果。"),
        ["正在缩小画板并采集原始颜色，随后会自动寻找目标。"] = ("缩小画板并采集", "縮小畫板並採集", "正在縮小畫板並採集原始顏色，隨後會自動尋找目標。"),
        ["正在覆盖重复色板，保留原始颜色点。"] = ("覆盖重复色板中", "覆蓋重複色板中", "正在覆蓋重複色板，保留原始顏色點。"),
        ["正在计算可执行的目标方案。"] = ("计算目标方案中", "計算目標方案中", "正在計算可執行的目標方案。"),
        ["正在计算平移、旋转与缩放方案；最终颜色以游戏实际色码为准。"] = ("计算移动方案中", "計算移動方案中", "正在計算平移、旋轉與縮放方案；最終顏色以遊戲實際色碼為準。"),
        ["等待游戏色板显示完整；可以先进入染色，再启动截图寻色。F9 可停止。"] = ("等待色板显示完整", "等待色板顯示完整", "等待遊戲色板顯示完整；可以先進入染色，再啟動截圖尋色。F9 可停止。"),
        ["正在准备寻色，请保持游戏窗口可见。F9 可停止。"] = ("保持游戏窗口可见", "保持遊戲視窗可見", "正在準備尋色，請保持遊戲視窗可見。F9 可停止。"),
        ["正在调整色板。"] = ("调整色板中", "調整色板中", "正在調整色板。"),
        ["正在核对当前游戏色码。"] = ("核对色码中", "核對色碼中", "正在核對目前遊戲色碼。")
    };

    /// <summary>引擎说明：界面显示短句，全文进悬停提示；未登记的说明显示“处理中”，原文进悬停提示并记入诊断页。</summary>
    internal static (string Body, string Tip) Message(string message)
    {
        if (Messages.TryGetValue(message, out var known)) return (Pick(known.ShortHans, known.ShortHant), Pick(message, known.FullHant));
        return (Localization.CodeText.Unknown("color-message", message, "处理中", "處理中"), message);
    }

    /// <summary>登记了短句的引擎说明（测试用来核对引擎里的全部说明都已登记）。</summary>
    internal static IReadOnlyCollection<string> KnownMessages => Messages.Keys;

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

    private static string JoinTip(params string[] parts) => string.Join(' ', parts.Where(part => part.Length > 0));
}
