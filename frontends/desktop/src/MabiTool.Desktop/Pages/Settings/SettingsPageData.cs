using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Pages.Settings;

/// <summary>
/// 一个全局快捷键：登记的按键、后端实际注册到的按键和注册状态。
/// <see cref="CanEdit"/> 表示后端接受修改这个动作的组合键。
/// </summary>
public sealed record ShortcutRow(string Action, string Chord, string State, bool Registered, string Id = "", bool CanEdit = false);

/// <summary>快捷键列表的一行加上界面的录入状态与最近一次修改的回执。</summary>
public sealed record ShortcutLine(ShortcutRow Row, bool Recording, bool CanStart, string Message, bool Failed)
{
    public bool Idle => !Recording;
    public bool HasMessage => Message.Length > 0;
}

/// <summary>
/// 设置页数据。<see cref="Policy"/> 是后端的更新策略，未连接或后端没有提供时为空，此时策略三项停用。
/// <see cref="AvailableRelease"/> 是后端更新状态里发现的新版本标识，没有新版本或未连接时为空。
/// <see cref="Connected"/> 表示已连接后端。
/// </summary>
public sealed record SettingsPageData(IReadOnlyList<KeyValue> Backend, IReadOnlyList<ShortcutRow> Shortcuts,
    IReadOnlyList<KeyValue> Update, bool CanCheckUpdate, bool CanDownloadUpdate = false, bool CanInstallUpdate = false,
    UpdatePolicyView? Policy = null, string? AvailableRelease = null, bool Connected = false);

/// <summary>更新策略的一个固定档位；<see cref="Active"/> 表示它是当前值。</summary>
public sealed record PolicyChoice(string Label, int Value, bool Active, string AccessibleName);

/// <summary>下载限速输入的单位：1 KB = 1,024 字节，1 MB = 1,048,576 字节。</summary>
public enum LimitUnit
{
    KB,
    MB
}

public sealed partial class SettingsPageModel : BackendPageModel<SettingsPageData>
{
    /// <summary>设置页没有自己的计时器：切走后保留，回来时直接显示并重读一次。</summary>
    protected override bool Cacheable => true;

    /// <summary>设置页“扩展”区的数据来源；表单由 <see cref="Controls.ExtensionSettingsView"/> 按后端的扩展界面目录生成。</summary>
    public IDesktopBackend ExtensionBackend => Backend;
    public string ExtensionSettingsTitle => UiText.Pick("扩展", "擴充");

    public SettingsPageModel(IDesktopBackend backend) : base(backend, source => source.GetSettings())
    {
        UseSimplifiedCommand = new RelayCommand(_ => Use(UiLanguage.SimplifiedChinese));
        UseTraditionalCommand = new RelayCommand(_ => Use(UiLanguage.TraditionalChinese));
        MotionOnCommand = new RelayCommand(_ => SetMotion(true));
        MotionOffCommand = new RelayCommand(_ => SetMotion(false));
        ThemeCommand = new RelayCommand(choice =>
        {
            if (Enum.TryParse<UiThemeChoice>(choice as string, out var value)) SetTheme(value);
        });
        RecordCommand = new RelayCommand(id =>
        {
            if (id is not string action || !Data.Shortcuts.Any(row => row.Id == action && row.CanEdit)) return;
            recording = action;
            messages.Remove(action);
            Raise(nameof(Lines));
            Raise(nameof(IsRecording));
        });
        CancelRecordCommand = new RelayCommand(_ => StopRecording());
        AutoCheckCommand = new RelayCommand(choice =>
        {
            if (Policy is { } policy) _ = SavePolicyAsync(policy with { AutomaticChecks = choice as string == "on" });
        });
        IntervalCommand = new RelayCommand(value =>
        {
            if (Policy is { } policy && value is int hours) _ = SavePolicyAsync(policy with { CheckIntervalHours = hours });
        });
        LimitUnitCommand = new RelayCommand(value =>
        {
            if (value is not string text || !Enum.TryParse<LimitUnit>(text, out var unit) || Data.Policy is null) return;
            if (unit != limitUnit)
            {
                limitUnit = unit;
                RaiseLimit();
            }
            // 切换单位时保留输入的数字，按新单位重新换算并提交。
            CommitLimit();
        });
        LimitModeCommand = new RelayCommand(value =>
        {
            if (Policy is not { } policy) return;
            if (value as string == "unlimited")
            {
                customDraft = false;
                if (policy.Unlimited) RaisePolicy();
                else _ = SavePolicyAsync(policy with { MaximumBytesPerSecond = null });
                return;
            }
            if (!policy.Unlimited) return;
            // 从不限速改为自定义：用输入框里的值；输入为空或不合法时先显示输入框，按回车或离开输入框时再提交。
            customDraft = true;
            RaisePolicy();
            if (ParseLimit(limitText, limitUnit) is not null) CommitLimit();
        });
        (limitText, limitUnit) = Describe(Data.Policy?.MaximumBytesPerSecond);
        shownLimitText = limitText;
        UiTheme.Changed += OnThemeChanged;
        InitializeCapture();
    }

    /// <summary>下载限速的“不限速／自定义”。</summary>
    public ICommand LimitModeCommand { get; }

    /// <summary>当前不限速（策略的带宽为空），且没有刚选了“自定义”、正在输入数值。</summary>
    public bool LimitUnlimited => Policy?.Unlimited == true && !customDraft;

    /// <summary>当前按自定义数值限速，或刚从不限速改选“自定义”：数值与单位只在这时显示。</summary>
    public bool LimitCustom => HasPolicy && !LimitUnlimited;

    /// <summary>“不限速”在后端提供策略时可选。</summary>
    public bool CanChooseUnlimited => HasPolicy;

    // 从不限速改选“自定义”后、数值保存之前为真：输入框显示，策略仍是不限速。
    private bool customDraft;

    // 检查间隔的档位取在后端校验范围之内：检查周期 1–168 小时。
    public static readonly IReadOnlyList<int> IntervalHours = [1, 6, 12, 24];

    /// <summary>后端接受的下载限速范围（字节每秒）。</summary>
    public const int MinimumBytesPerSecond = 65_536;
    public const int MaximumBytesPerSecond = 67_108_864;
    private const int Kilobyte = 1_024;
    private const int Megabyte = 1_048_576;

    private UpdatePolicyView? pendingPolicy;
    private string limitText;
    private string shownLimitText;
    private LimitUnit limitUnit;
    private bool limitInvalid;
    private bool resetLimit;

    public ICommand AutoCheckCommand { get; }
    public ICommand IntervalCommand { get; }
    public ICommand LimitUnitCommand { get; }

    /// <summary>下载限速输入框的内容。按回车、输入框失去焦点或切换单位时提交（<see cref="CommitLimit"/>）。</summary>
    public string LimitText
    {
        get => limitText;
        set
        {
            value ??= string.Empty;
            if (limitText == value) return;
            limitText = value;
            Raise(nameof(LimitText));
        }
    }

    public LimitUnit Unit => limitUnit;
    public bool LimitInKilobytes => limitUnit == LimitUnit.KB;
    public bool LimitInMegabytes => limitUnit == LimitUnit.MB;

    /// <summary>输入为空、不是数字或超出后端范围：不发送，在该项下方显示范围说明。</summary>
    public bool LimitInvalid => limitInvalid;

    /// <summary>按当前单位写出的范围说明。</summary>
    public string LimitRange => limitUnit == LimitUnit.KB
        ? UiText.Pick($"请输入 {MinimumBytesPerSecond / Kilobyte}–{RangeMaximumKilobytes} KB/s 之间的数字",
            $"請輸入 {MinimumBytesPerSecond / Kilobyte}–{RangeMaximumKilobytes} KB/s 之間的數字")
        : UiText.Pick($"请输入 {RangeMinimumMegabytes}–{MaximumBytesPerSecond / Megabyte} MB/s 之间的数字",
            $"請輸入 {RangeMinimumMegabytes}–{MaximumBytesPerSecond / Megabyte} MB/s 之間的數字");

    private static string RangeMaximumKilobytes =>
        (MaximumBytesPerSecond / Kilobyte).ToString("N0", CultureInfo.InvariantCulture);

    // 64 KB/s 以 MB/s 表示是 0.0625，按两位小数向上取整写成 0.07，保证写出的下限本身在范围内。
    private static string RangeMinimumMegabytes =>
        (Math.Ceiling((double)MinimumBytesPerSecond / Megabyte * 100) / 100).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>把后端的字节每秒换成显示用的数字与单位：能整除 1 MB 时用 MB/s，否则用 KB/s，小数最多两位。</summary>
    public static (string Text, LimitUnit Unit) Describe(int? bytes)
    {
        if (bytes is not { } value) return (string.Empty, LimitUnit.MB);
        return value % Megabyte == 0
            ? ((value / Megabyte).ToString(CultureInfo.InvariantCulture), LimitUnit.MB)
            : (((double)value / Kilobyte).ToString("0.##", CultureInfo.InvariantCulture), LimitUnit.KB);
    }

    /// <summary>把输入换算成字节每秒；为空、不是数字或超出后端范围时返回空。</summary>
    public static int? ParseLimit(string? text, LimitUnit unit)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
            return null;
        var bytes = Math.Round(number * (unit == LimitUnit.KB ? Kilobyte : Megabyte));
        return bytes is >= MinimumBytesPerSecond and <= MaximumBytesPerSecond ? (int)bytes : null;
    }

    /// <summary>
    /// 提交下载限速：输入合法且与后端当前值不同才保存；不合法时不发送，保留输入并显示范围说明。
    /// </summary>
    public void CommitLimit()
    {
        if (Policy is not { } policy || policy.Unlimited && !customDraft) return;
        if (ParseLimit(limitText, limitUnit) is not { } bytes)
        {
            limitInvalid = true;
            RaiseLimit();
            return;
        }
        limitInvalid = false;
        RaiseLimit();
        if (bytes == policy.MaximumBytesPerSecond) return;
        resetLimit = true;
        _ = SavePolicyAsync(policy with { MaximumBytesPerSecond = bytes });
    }

    private void RaiseLimit()
    {
        foreach (var name in new[]
                 {
                     nameof(LimitText), nameof(Unit), nameof(LimitInKilobytes), nameof(LimitInMegabytes), nameof(LimitInvalid),
                     nameof(LimitRange)
                 })
            Raise(name);
    }

    // 页面每次重读时同步输入框：用户改过但尚未提交、或输入不合法时保留输入；保存下载限速结束后一律回到后端值。
    // 后端不限速时输入框保留上次的数值，改选“自定义”时直接沿用。
    private void SyncLimit()
    {
        if (pendingPolicy is not null) return;
        if (Data.Policy is { Unlimited: false }) customDraft = false;
        if (!resetLimit && (limitInvalid || limitText != shownLimitText)) return;
        resetLimit = false;
        limitInvalid = false;
        if (Data.Policy?.MaximumBytesPerSecond is null) return;
        (limitText, limitUnit) = Describe(Data.Policy.MaximumBytesPerSecond);
        shownLimitText = limitText;
    }

    /// <summary>界面显示的更新策略：保存进行中显示新值，其余时候是后端的当前值。</summary>
    public UpdatePolicyView? Policy => pendingPolicy ?? Data.Policy;

    /// <summary>后端提供了策略时三项可以操作；检查间隔只在自动检查开启时可改。</summary>
    public bool HasPolicy => Data.Policy is not null;
    public bool CanEditInterval => HasPolicy && Policy?.AutomaticChecks == true;
    public bool AutoCheckOn => Policy?.AutomaticChecks == true;
    public bool AutoCheckOff => Policy is { AutomaticChecks: false };
    public bool PolicyUnavailable => Data.Policy is null;

    /// <summary>检查间隔的档位；后端的当前值不在档位里时作为额外一档列出。</summary>
    public IReadOnlyList<PolicyChoice> IntervalChoices => Choices(IntervalHours, Policy?.CheckIntervalHours,
        hours => hours.ToString(CultureInfo.InvariantCulture),
        hours => UiText.Pick($"每 {hours} 小时检查一次", $"每 {hours} 小時檢查一次"));

    private static List<PolicyChoice> Choices(IReadOnlyList<int> values, int? current, Func<int, string> label, Func<int, string> name)
    {
        var all = current is { } value && !values.Contains(value) ? [.. values, value] : values.ToList();
        return all.Order().Select(item => new PolicyChoice(label(item), item, item == current, name(item))).ToList();
    }

    // 改动即保存。保存期间显示新值；结束后重读：成功时是新值，失败时回到后端当前值，原因由后端显示在活动胶囊。
    private async Task SavePolicyAsync(UpdatePolicyView next)
    {
        // 同一时间只保存一项，保存中的再次点击不发送。
        if (Data.Policy is null || pendingPolicy is not null || next == Data.Policy) return;
        pendingPolicy = next;
        RaisePolicy();
        try { await Backend.SaveUpdatePolicyAsync(next, CancellationToken.None); }
        finally
        {
            pendingPolicy = null;
            Reload();
        }
    }

    private void RaisePolicy()
    {
        foreach (var name in new[]
                 {
                     nameof(Policy), nameof(CanEditInterval), nameof(AutoCheckOn), nameof(AutoCheckOff),
                     nameof(IntervalChoices), nameof(LimitUnlimited), nameof(LimitCustom), nameof(CanChooseUnlimited)
                 })
            Raise(name);
    }

    public override void Dispose()
    {
        UiTheme.Changed -= OnThemeChanged;
        base.Dispose();
    }

    // 主题也可能由窗口右上角的按钮切换，三段开关随之同步。
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
        Raise(nameof(ThemeSystem));
    }

    private readonly Dictionary<string, (string Text, bool Failed)> messages = new(StringComparer.Ordinal);
    private string? recording;

    public ICommand RecordCommand { get; }
    public ICommand CancelRecordCommand { get; }
    public bool IsRecording => recording is not null;

    /// <summary>快捷键列表；紧急停止排在最前，始终可见。</summary>
    public IReadOnlyList<ShortcutLine> Lines => Data.Shortcuts
        .OrderBy(row => row.Id == "emergency-stop" ? 0 : 1)
        .Select(row => messages.TryGetValue(row.Id, out var message)
            ? new ShortcutLine(row, row.Id == recording, row.CanEdit && recording is null, message.Text, message.Failed)
            : new ShortcutLine(row, row.Id == recording, row.CanEdit && recording is null, string.Empty, false))
        .ToList();

    /// <summary>
    /// 录入状态下收到的按键。只按修饰键时继续等待；按下其他键即提交给后端，后端接受或拒绝都把回执显示在这一行。
    /// 返回是否处理了这次按键。
    /// </summary>
    public async Task<bool> OnKeyAsync(Key key, KeyModifiers modifiers)
    {
        if (recording is not { } action) return false;
        if (KeyName(key) is not { } name) return true;
        var chord = new KeyChord(name, modifiers.HasFlag(KeyModifiers.Control), modifiers.HasFlag(KeyModifiers.Alt),
            modifiers.HasFlag(KeyModifiers.Shift), modifiers.HasFlag(KeyModifiers.Meta));
        StopRecording();
        var reply = await Backend.ChangeShortcutAsync(action, chord, CancellationToken.None);
        messages[action] = (reply.Message, !reply.Ok);
        Raise(nameof(Lines));
        return true;
    }

    /// <summary>把按键换成后端快捷键表的键名；修饰键本身返回空。键名是否受支持由后端判定。</summary>
    public static string? KeyName(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None => null,
        >= Key.D0 and <= Key.D9 => key.ToString(),
        Key.Back => "Backspace",
        Key.Return => "Enter",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        _ => key.ToString()
    };

    protected override void OnDataChanged()
    {
        ResetCapture();
        SyncLimit();
        if (recording is not null && !Data.Shortcuts.Any(row => row.Id == recording && row.CanEdit)) recording = null;
    }

    private void StopRecording()
    {
        recording = null;
        Raise(nameof(Lines));
        Raise(nameof(IsRecording));
    }

    public ICommand ThemeCommand { get; }
    public bool ThemeLight => UiTheme.Choice == UiThemeChoice.Light;
    public bool ThemeDark => UiTheme.Choice == UiThemeChoice.Dark;
    public bool ThemeSystem => UiTheme.Choice == UiThemeChoice.System;

    // 换主题不重建页面：令牌以动态资源跟随，这里只刷新分段的选中状态。
    private void SetTheme(UiThemeChoice choice)
    {
        if (UiTheme.Choice != choice && Application.Current is { } application) UiTheme.Apply(application, choice);
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
        Raise(nameof(ThemeSystem));
    }

    public ICommand UseSimplifiedCommand { get; }
    public ICommand UseTraditionalCommand { get; }
    public ICommand MotionOnCommand { get; }
    public ICommand MotionOffCommand { get; }
    public bool MotionOn => UiMotion.Enabled;
    public bool MotionOff => !UiMotion.Enabled;
    public bool IsSimplified => UiText.Language == UiLanguage.SimplifiedChinese;
    public bool IsTraditional => UiText.Language == UiLanguage.TraditionalChinese;
    public bool NoShortcuts => Data.Shortcuts.Count == 0;

    public IReadOnlyList<KeyValue> About =>
    [
        new(UiText.Pick("名称", "名稱"), "Moongate"),
        new(UiText.Pick("界面字体", "介面字型"), "Noto Sans SC / Noto Sans TC，SIL OFL 1.1"),
        new(UiText.Pick("许可", "授權"), "MIT，MinamiChiwa（南千和）")
    ];

    private void SetMotion(bool enabled)
    {
        UiMotion.Enabled = enabled;
        Raise(nameof(MotionOn));
        Raise(nameof(MotionOff));
    }

    // 换语言后外壳会重建当前页面，所以这里不必自己刷新。
    private static void Use(UiLanguage language)
    {
        if (UiText.Language != language && Application.Current is { } application) UiText.Apply(application, language);
    }
}
