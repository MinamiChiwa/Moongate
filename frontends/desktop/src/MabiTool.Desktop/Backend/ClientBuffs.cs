using System.Runtime.InteropServices;
using MabiTool.Desktop.Pages.Combat;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>
/// 客户端 Buff 表的一项（后端 <c>GET /api/v1/combat/client-buffs</c>）：效果编号、名称原文、极性与描述（没有时为空）。
/// <see cref="SearchKey"/> 是名称的搜索归一化形式（<see cref="ChineseSearch.Fold"/>），建表时算一次。
/// </summary>
public sealed record ClientBuffEntry(string Id, string Name, EffectPolarity Polarity, string? Description)
{
    public string SearchKey { get; } = ChineseSearch.Fold(Name);
}

/// <summary>
/// 收藏层可选范围里来自客户端的部分：<see cref="Entries"/> 只在读到时有内容；<see cref="Loading"/> 为正在读取，
/// <see cref="Problem"/> 为读不到时的中文原因（这时收藏层只用本机观测到的目录）。
/// </summary>
public sealed record ClientBuffList(IReadOnlyList<ClientBuffEntry> Entries, bool Loading, string? Problem)
{
    /// <summary>尚未读取（收藏层第一次打开前）。</summary>
    public static ClientBuffList NotRead { get; } = new([], false, null);

    /// <summary>后端适配层不提供客户端 Buff 表（样本数据与测试替身）。</summary>
    public static ClientBuffList Unsupported => new([], false, Pick("当前数据来源没有客户端 Buff 表", "目前資料來源沒有用戶端 Buff 表"));

    public bool Ready => Entries.Count > 0;
}

/// <summary>搜索用的中文归一化：繁体字按字转成简体（Windows LCMapStringEx），再转小写，简繁输入都能匹配客户端原文。</summary>
public static class ChineseSearch
{
    private const uint SimplifiedChinese = 0x02000000;

    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lower = text.ToLowerInvariant();
        if (!OperatingSystem.IsWindows()) return lower;
        try
        {
            var buffer = new char[lower.Length];
            var written = LCMapStringEx("zh-CN", SimplifiedChinese, lower, lower.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return written == lower.Length ? new string(buffer) : lower;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return lower; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength, [Out] char[] destination,
        int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);
}
