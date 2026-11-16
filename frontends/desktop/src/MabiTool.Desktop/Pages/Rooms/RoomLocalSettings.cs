using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>开始方式，与 <see cref="RoomPreferences.StartInputKinds"/> 的取值一致：快捷键、游戏按钮、预填指令（只用于表情动作）。</summary>
public enum RoomStartKind { Key = 0, Button = 1, ChatDraft = 2 }

/// <summary>
/// 本机动作与房主本轮设置的读写规则。所有设置都存放在后端的 <see cref="RoomPreferences"/>，改动即保存完整设置；
/// 这里给出原工具的默认值、秒数输入的解析和绑定的改写，均为纯函数。
/// </summary>
public static class RoomLocalSettings
{
    /// <summary>空格键的 Windows 虚拟键码，原工具合奏停止的默认按键。</summary>
    public const ushort SpaceKey = 0x20;

    /// <summary>
    /// 显示与保存用的设置。修订为 0 表示后端还没有保存过任何设置，此时按原工具的默认值补齐后端默认值没有覆盖的部分：
    /// 表情的开始为预填指令；合奏开始为游戏按钮、停止为空格键（联机方式的默认值服务器直连由后端给出）。保存过的设置原样返回。
    /// </summary>
    public static RoomPreferences Effective(RoomPreferences value)
    {
        if (value.Revision.Value != 0) return value;
        var bindings = new Dictionary<string, ModeInputPreferences>(value.Bindings, StringComparer.Ordinal);
        bindings["emote"] = new(bindings.GetValueOrDefault("emote")?.Start ?? new(LocalBindingKind.ChatDraft), null, false);
        foreach (var mode in new[] { "independent", "ensemble" })
        {
            var current = bindings.GetValueOrDefault(mode);
            bindings[mode] = new(current?.Start, current?.Stop ?? new(LocalBindingKind.Key, SpaceKey), false);
        }
        var kinds = new Dictionary<string, int>(value.StartInputKinds, StringComparer.Ordinal)
        {
            ["emote"] = (int)RoomStartKind.ChatDraft, ["independent"] = (int)RoomStartKind.Button, ["ensemble"] = (int)RoomStartKind.Button
        };
        return value with { Bindings = bindings, StartInputKinds = kinds };
    }

    public static RoomStartKind StartKind(RoomPreferences value, string mode)
    {
        var kind = (RoomStartKind)value.StartInputKinds.GetValueOrDefault(mode);
        return mode != "emote" && kind == RoomStartKind.ChatDraft ? RoomStartKind.Button : kind;
    }

    public static InputBindingPreference? Start(RoomPreferences value, string mode) => value.Bindings.GetValueOrDefault(mode)?.Start;
    public static InputBindingPreference? Stop(RoomPreferences value, string mode) => value.Bindings.GetValueOrDefault(mode)?.Stop;

    /// <summary>
    /// 改开始方式。预填指令写入聊天草稿绑定；快捷键或游戏按钮保留同类的原绑定，原绑定不是这一类时清空，等待录入或点选。
    /// </summary>
    public static RoomPreferences WithStartKind(RoomPreferences value, string mode, RoomStartKind kind, InputBindingPreference? remembered = null)
    {
        var current = Start(value, mode);
        var start = kind switch
        {
            RoomStartKind.ChatDraft => new InputBindingPreference(LocalBindingKind.ChatDraft),
            _ when current?.Kind.KnownValue == KindOf(kind) => current,
            _ when remembered?.Kind.KnownValue == KindOf(kind) => remembered,
            _ => null
        };
        var kinds = new Dictionary<string, int>(value.StartInputKinds, StringComparer.Ordinal) { [mode] = (int)kind };
        return WithBinding(value, mode, start, Stop(value, mode)) with { StartInputKinds = kinds };
    }

    public static RoomPreferences WithStart(RoomPreferences value, string mode, InputBindingPreference? start) =>
        WithBinding(value, mode, start, Stop(value, mode));

    public static RoomPreferences WithStop(RoomPreferences value, string mode, InputBindingPreference? stop) =>
        WithBinding(value, mode, Start(value, mode), mode == "emote" ? null : stop);

    // 原工具的“停止与开始同键”已经不用；开始与停止相同由 BindingError 提示，不保存成切换绑定。
    private static RoomPreferences WithBinding(RoomPreferences value, string mode, InputBindingPreference? start, InputBindingPreference? stop)
    {
        var bindings = new Dictionary<string, ModeInputPreferences>(value.Bindings, StringComparer.Ordinal) { [mode] = new(start, stop, false) };
        return value with { Bindings = bindings };
    }

    private static LocalBindingKind KindOf(RoomStartKind kind) => kind switch
    {
        RoomStartKind.Button => LocalBindingKind.Button,
        RoomStartKind.ChatDraft => LocalBindingKind.ChatDraft,
        _ => LocalBindingKind.Key
    };

    /// <summary>乐谱使用者的开始与停止必须不同（原工具 ValidateLocalReadiness）；参与者不触发开始，不检查。</summary>
    public static string? BindingError(RoomPreferences value, string mode, bool joiner)
    {
        if (mode == "emote" || joiner) return null;
        var start = Start(value, mode);
        return start is not null && start == Stop(value, mode)
            ? UiText.Pick("开始和停止需要使用不同的按键或按钮，请重新设置停止操作。", "開始和停止需要使用不同的按鍵或按鈕，請重新設定停止操作。")
            : null;
    }

    /// <summary>按键绑定的显示：组合键；没有时为空。</summary>
    public static string KeyText(InputBindingPreference? binding) => binding is { Kind.KnownValue: LocalBindingKind.Key, Key: { } key }
        ? string.Join(" + ", new[]
        {
            binding.Control ? "Ctrl" : null, binding.Alt ? "Alt" : null, binding.Shift ? "Shift" : null, VirtualKeys.Name(key)
        }.Where(part => part is not null))
        : string.Empty;

    /// <summary>游戏按钮的记录说明（原工具 UpdateBindingDisplay）。</summary>
    public static string ButtonText(InputBindingPreference? binding) => binding is { Kind.KnownValue: LocalBindingKind.Button }
        ? UiText.Pick($"已记录 ({binding.X}, {binding.Y}) · {binding.ClientWidth}×{binding.ClientHeight} · DPI {binding.Dpi}",
            $"已記錄 ({binding.X}, {binding.Y}) · {binding.ClientWidth}×{binding.ClientHeight} · DPI {binding.Dpi}")
        : UiText.Pick("尚未选定按钮位置", "尚未選定按鈕位置");

    /// <summary>倒计时：1–60 秒，最多三位小数。</summary>
    public static int? ParseCountdown(string text, out string? error) =>
        ParseSeconds(text, 1, 60, out error, UiText.Pick("倒计时请填写 1–60 秒，最多三位小数，例如 5 或 2.5。", "倒數請填寫 1–60 秒，最多三位小數，例如 5 或 2.5。"));

    /// <summary>指定演奏时长：0.001–86400 秒，最多三位小数。</summary>
    public static int? ParseDuration(string text, out string? error) =>
        ParseSeconds(text, 0.001m, 86400, out error, UiText.Pick("演奏时长请填写 0.001–86400 秒，最多三位小数，例如 30 或 12.5。",
            "演奏時長請填寫 0.001–86400 秒，最多三位小數，例如 30 或 12.5。"));

    /// <summary>尾音保留：0–10 秒，最多三位小数。</summary>
    public static int? ParseTail(string text, out string? error) =>
        ParseSeconds(text, 0, 10, out error, UiText.Pick("尾音保留请填写 0–10 秒，最多三位小数，例如 1 或 1.5。", "尾音保留請填寫 0–10 秒，最多三位小數，例如 1 或 1.5。"));

    /// <summary>本机时间微调：−2000～+2000 的整数毫秒。</summary>
    public static int? ParseOffset(string text, out string? error)
    {
        error = null;
        if (int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value is >= -2000 and <= 2000)
            return value;
        error = UiText.Pick("时间微调请输入 −2000～+2000 的整数毫秒；负数提前，正数延后。", "時間微調請輸入 −2000～+2000 的整數毫秒；負數提前，正數延後。");
        return null;
    }

    private static int? ParseSeconds(string text, decimal min, decimal max, out string? error, string message)
    {
        error = null;
        if (decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= min && seconds <= max && seconds * 1000 == decimal.Truncate(seconds * 1000))
            return (int)(seconds * 1000);
        error = message;
        return null;
    }

    /// <summary>毫秒写成秒，最多三位小数。</summary>
    public static string Seconds(int milliseconds) => (milliseconds / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>时间微调的说明：负数提前、正数延后、0 不调整。</summary>
    public static string OffsetText(int milliseconds) => milliseconds switch
    {
        < 0 => UiText.Pick($"{milliseconds} ms · 提前", $"{milliseconds} ms · 提前"),
        > 0 => UiText.Pick($"+{milliseconds} ms · 延后", $"+{milliseconds} ms · 延後"),
        _ => UiText.Pick("0 ms · 不调整", "0 ms · 不調整")
    };
}

/// <summary>房间邀请：原工具的 <c>MabiSync/1|地址|房间码</c>，或只有六位房间码。</summary>
public static class RoomInvitationText
{
    public static string Format(string endpoint, string code) => $"MabiSync/1|{endpoint}|{code}";

    /// <summary>六位字母或数字的房间码。</summary>
    public static bool IsCode(string text) => text.Length == 6 && text.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// 解析输入框的内容。完整邀请返回邀请里的地址；只有房间码时地址为空，由联机方式决定。
    /// 不是这两种格式时返回空并给出说明。
    /// </summary>
    public static (string? Endpoint, string Code)? Parse(string text, out string? error)
    {
        error = null;
        var value = text.Trim();
        if (IsCode(value)) return (null, value.ToUpperInvariant());
        var parts = value.Split('|');
        if (parts.Length == 3 && parts[0] == "MabiSync/1" && Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) &&
            uri.Scheme is "ws" or "wss" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && IsCode(parts[2]))
            return (parts[1], parts[2].ToUpperInvariant());
        error = UiText.Pick("邀请不完整，请复制房主分享的整条邀请。只有房间码时，请先选择与房主相同的联机方式。",
            "邀請不完整，請複製房主分享的整條邀請。只有房間碼時，請先選擇與房主相同的連線方式。");
        return null;
    }
}
