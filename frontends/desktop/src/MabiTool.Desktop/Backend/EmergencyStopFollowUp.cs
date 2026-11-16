using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>
/// 紧急停止之后前端要做的事。后端已撤销本工具的游戏输入、送出合奏的本机停止键并取消寻色；
/// 进行中的定时测量由前端经战斗页已有的取消入口（<c>combat.measure.cancel</c>）取消。
/// <see cref="Text"/> 写进合奏反馈横幅与活动胶囊；<see cref="Failed"/> 表示后端回执里有未完成的部分。
/// </summary>
public sealed record EmergencyStopFollowUp(IReadOnlyList<string> Intents, string Text, bool Failed)
{
    public const string Hotkey = "hotkey";
    public const string Button = "button";

    /// <param name="source">由快捷键还是界面按钮触发。</param>
    /// <param name="measurementPhase">当前定时测量阶段；waiting（点开始后等待首次伤害）与 running 表示进行中。</param>
    /// <param name="receipt">按钮触发时的后端回执；快捷键由后端直接执行，没有回执。</param>
    public static EmergencyStopFollowUp Plan(string source, string? measurementPhase, GameEmergencyStopReceipt? receipt)
    {
        var measuring = measurementPhase is "waiting" or "running";
        var origin = source == Hotkey ? Pick("快捷键", "快捷鍵") : Pick("按钮", "按鈕");
        var parts = new List<string> { Pick("已中止本工具的游戏输入与自动操作（演奏中的合奏先送出本机停止，寻色与待执行的合奏动作已取消）",
            "已中止本工具的遊戲輸入與自動操作（演奏中的合奏先送出本機停止，尋色與待執行的合奏動作已取消）") };
        if (measuring) parts.Add(Pick("正在取消定时测量", "正在取消定時測量"));
        if (receipt is not null)
            parts.Add(Pick($"撤销 {receipt.RevokedLeases} 个输入占用", $"撤銷 {receipt.RevokedLeases} 個輸入佔用"));
        var failed = receipt is { Failures.Count: > 0 } || receipt is { InputReleaseState: not "released" };
        if (receipt is { Failures.Count: > 0 } value)
            parts.Add(Pick($"未完成：{string.Join("、", value.Failures.Select(item => item.Component + " " + item.Code))}",
                $"未完成：{string.Join("、", value.Failures.Select(item => item.Component + " " + item.Code))}"));
        var text = Pick($"已紧急停止（{origin}）：", $"已緊急停止（{origin}）：") + string.Join(Pick("；", "；"), parts) + Pick("。", "。");
        return new(measuring ? ["combat.measure.cancel"] : [], text, failed);
    }
}
