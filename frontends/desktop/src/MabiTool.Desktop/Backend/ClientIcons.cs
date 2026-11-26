using Avalonia.Media.Imaging;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>
/// 一个可用的客户端属性图标：已解码的白色字形（64×64）、目录给出的着色（#RRGGBB，可为空）与显示名称原文。
/// <see cref="Version"/> 是 PNG 的 SHA-256，版本不变时同一位图一直沿用。
/// </summary>
public sealed record ClientIconImage(string Key, string Version, Bitmap Bitmap, string? Tint, string? DisplayName);

/// <summary>
/// 图标键（与后端目录 <c>/api/v1/client-icons</c> 的键相同）对应的工具自己的名称与缺省颜色。
/// 名称跟随界面语言，放进图标的悬浮提示与无障碍名称；目录里的显示名称只在这里没有名称时使用。
/// </summary>
public static class ClientIconNames
{
    public const string Critical = "critical", PowerfulHit = "powerfulHit", RapidHit = "rapidHit", DoubleHit = "doubleHit",
        BreakedHit = "breakedHit", BreakOccurred = "breakOccurred", MultiHit = "multiHit";

    /// <summary>无防备在客户端表里没有颜色，沿用破防的颜色。</summary>
    public const string BreakTint = "#FF5D41";

    /// <summary>元素的图标键：<c>element.</c> 加元素名（bleed、dark、fire、holy、ice、lightning、poison、soul）。</summary>
    public static string Element(string element) => "element." + element.ToLowerInvariant();

    /// <summary>图标的名称；不认识的键为空串。</summary>
    public static string Name(string? key) => key switch
    {
        Critical => Pick("暴击", "暴擊"),
        PowerfulHit => Pick("强击", "強擊"),
        RapidHit => Pick("连击", "連擊"),
        DoubleHit => Pick("追击", "追擊"),
        BreakedHit => Pick("无防备", "無防備"),
        BreakOccurred => Pick("破防", "破防"),
        MultiHit => Pick("多重打击", "多重打擊"),
        { } element when element.StartsWith("element.", StringComparison.Ordinal) => CombatMapper.ElementName(element["element.".Length..]),
        _ => string.Empty
    };

    /// <summary>目录没有给出颜色时的着色：无防备取破防色，元素取该元素的固定颜色，其余为空（由界面用前景色）。</summary>
    public static string? DefaultTint(string key) => key switch
    {
        BreakedHit => BreakTint,
        _ when key.StartsWith("element.", StringComparison.Ordinal) => CombatMapper.ElementColor(key["element.".Length..]),
        _ => null
    };
}

/// <summary>
/// 界面各处的图标位置与后端之间的中转：主窗口组装时指定后端（<see cref="Use"/>）。取用顺序：先取桌面内嵌的随包图标
/// （<see cref="Theme.GameIcons.Attribute"/>，没有绑定游戏时也可用），包里没有的键再取后端运行时从本机客户端读到的图标。
/// 图标位置挂上可见窗口时登记使用、移出时注销；只有包里缺键（<see cref="NeedsRuntime"/>）且有登记时才让后端读取目录与下载图标，
/// 包里键齐全时后端不读取。后端的图标有变化时 <see cref="Changed"/> 在界面线程上通知各位置重取。
/// </summary>
public static class ClientIconHub
{
    private static IDesktopBackend? source;
    private static int users;
    private static bool bundled = true;
    // 是否已经让后端读取运行时图标（有使用登记且包里缺键）；撤销时按它撤销，不因中途包的状态变化漏掉。
    private static bool forwarded;

    /// <summary>
    /// 是否先取随包图标。测试用：置为假时只取后端的运行时图标（与包里缺全部键相同），用于覆盖运行时后备的行为。
    /// </summary>
    internal static bool UseBundled
    {
        get => bundled;
        set
        {
            if (bundled == value) return;
            bundled = value;
            Sync();
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>随包图标缺少至少一个属性键，需要后端运行时读取补上。</summary>
    public static bool NeedsRuntime => !bundled || Theme.GameIcons.MissingAttributes(MabiTool.Contracts.ClientIconKeys.All).Count > 0;

    public static event EventHandler? Changed;

    /// <summary>当前的后端；为空时所有位置显示文字。</summary>
    public static IDesktopBackend? Source => source;

    /// <summary>界面上正在使用图标的位置个数。</summary>
    public static int Users => users;

    /// <summary>换用后端：已有的使用登记转到新后端，各位置随即重取。</summary>
    public static void Use(IDesktopBackend? backend)
    {
        if (ReferenceEquals(source, backend)) return;
        if (source is { } old)
        {
            old.ClientIconsChanged -= OnSourceChanged;
            if (forwarded) old.SetClientIconsInUse(false);
        }
        forwarded = false;
        source = backend;
        if (backend is not null) backend.ClientIconsChanged += OnSourceChanged;
        Sync();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>键对应的可用图标：先取随包图标，包里没有时取后端的运行时图标；都没有时为空（界面显示文字）。</summary>
    public static ClientIconImage? Get(string key) => (bundled ? Theme.GameIcons.Attribute(key) : null) ?? source?.GetClientIcon(key);

    internal static void Acquire()
    {
        users++;
        Sync();
    }

    internal static void Release()
    {
        if (users == 0) return;
        users--;
        Sync();
    }

    // 有使用登记且包里缺键时让后端读取运行时图标，否则撤销；状态没变不重复通知后端。
    private static void Sync()
    {
        var wanted = users > 0 && source is not null && NeedsRuntime;
        if (wanted == forwarded) return;
        forwarded = wanted;
        source?.SetClientIconsInUse(wanted);
    }

    private static void OnSourceChanged(object? sender, EventArgs e) => Changed?.Invoke(null, EventArgs.Empty);
}
