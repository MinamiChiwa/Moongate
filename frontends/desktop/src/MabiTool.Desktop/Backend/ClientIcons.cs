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
/// 界面各处的图标位置与后端之间的中转：主窗口组装时指定后端（<see cref="Use"/>）。图标位置挂上可见窗口时登记使用、
/// 移出时注销，有登记时后端才读取目录与下载图标；后端的图标有变化时 <see cref="Changed"/> 在界面线程上通知各位置重取。
/// </summary>
public static class ClientIconHub
{
    private static IDesktopBackend? source;
    private static int users;

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
            if (users > 0) old.SetClientIconsInUse(false);
        }
        source = backend;
        if (backend is not null)
        {
            backend.ClientIconsChanged += OnSourceChanged;
            if (users > 0) backend.SetClientIconsInUse(true);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>键对应的可用图标；后端没有、目录未就绪或该键不可用时为空。</summary>
    public static ClientIconImage? Get(string key) => source?.GetClientIcon(key);

    internal static void Acquire()
    {
        if (++users == 1) source?.SetClientIconsInUse(true);
    }

    internal static void Release()
    {
        if (users == 0) return;
        if (--users == 0) source?.SetClientIconsInUse(false);
    }

    private static void OnSourceChanged(object? sender, EventArgs e) => Changed?.Invoke(null, EventArgs.Empty);
}
