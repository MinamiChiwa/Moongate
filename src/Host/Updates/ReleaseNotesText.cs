using System.Text.RegularExpressions;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.Host.Updates;

/// <summary>
/// 更新说明文本的整理，供界面直接排版：去掉发布脚本用的注释标记，去掉页首的一级标题与语言导航行（界面自己写版本标题），
/// 相对链接按文件所在页换成绝对地址，只保留 http／https 链接，图片只留替代文字。
/// 公开仓库里的文件按语言各一份；Release 正文是简体、繁体合在一起的，由 <see cref="SplitReleaseBody"/> 按发布脚本的合成顺序切开。
/// </summary>
internal static partial class ReleaseNotesText
{
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    [GeneratedRegex(@"!\[(?<alt>[^\]\n]*)\]\([^)\n]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[(?<text>[^\]\n]*)\]\((?<target>[^)\s]*)(?:\s+""[^""\n]*"")?\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    // 整行只有链接（以 · 或 | 分隔）：文档页首的语言与导航行。
    [GeneratedRegex(@"^\s*(?:\[[^\]\n]+\]\([^)\n]*\)\s*[·|•]?\s*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex NavigationLine();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankRuns();

    private static string Unify(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');

    /// <summary>
    /// 整理公开仓库里的一份更新说明；<paramref name="page"/> 是该文件在公开仓库的页面地址，相对链接以它为基准。
    /// 整理后为空或超过 <see cref="UpdatePlanStore.MaximumNotesCharacters"/> 个字符时返回空。
    /// </summary>
    internal static string? Normalize(string text, Uri page)
    {
        var lines = Comment().Replace(Unify(text), string.Empty).Split('\n').ToList();
        DropLeadingBlank(lines);
        if (lines.Count > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
            DropLeadingBlank(lines);
            if (lines.Count > 0 && NavigationLine().IsMatch(lines[0])) lines.RemoveAt(0);
        }
        return Finish(string.Join('\n', lines), page);
    }

    /// <summary>
    /// 把 Release 正文切成简体与繁体两份。正文由发布脚本合成：两份 <c>release-header</c> 段（简体在前，块数相同）、分隔线、
    /// 简体的“## 标题”与内容、分隔线、繁体的“## 标题”与内容；语言标题是紧跟在分隔线后的二级标题。
    /// 不符合这个结构（找不到两个语言标题）时两份都为空；两份 <c>release-header</c> 无法按块数对半分开时只保留正文。
    /// </summary>
    internal static (string? Simplified, string? Traditional) SplitReleaseBody(string body, Uri page)
    {
        var lines = Unify(body).Split('\n');
        var headings = new List<(int Heading, int Rule)>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("## ", StringComparison.Ordinal)) continue;
            var previous = index - 1;
            while (previous >= 0 && lines[previous].Trim().Length == 0) previous--;
            if (previous >= 0 && IsRule(lines[previous])) headings.Add((index, previous));
        }
        if (headings.Count < 2) return (null, null);
        var header = SplitHeader(lines[..headings[0].Rule]);
        string Section(int from, int to, string leading)
        {
            var content = string.Join('\n', lines[from..to]);
            return Finish(leading.Length == 0 ? content : leading + "\n\n" + content, page) ?? string.Empty;
        }
        var simplified = Section(headings[0].Heading + 1, headings[1].Rule, header.Simplified);
        var traditional = Section(headings[1].Heading + 1, lines.Length, header.Traditional);
        return (simplified.Length == 0 ? null : simplified, traditional.Length == 0 ? null : traditional);
    }

    private static bool IsRule(string line) => line.Trim() is "---" or "***" or "___";

    // 两份 release-header 以空行分块、块数相同：前一半为简体，后一半为繁体。
    private static (string Simplified, string Traditional) SplitHeader(string[] lines)
    {
        var blocks = Regex.Split(string.Join('\n', lines).Trim(), @"\n\s*\n").Where(block => block.Trim().Length > 0).ToList();
        if (blocks.Count < 2 || blocks.Count % 2 != 0) return (string.Empty, string.Empty);
        var half = blocks.Count / 2;
        return (string.Join("\n\n", blocks.Take(half)), string.Join("\n\n", blocks.Skip(half)));
    }

    private static void DropLeadingBlank(List<string> lines)
    {
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
    }

    private static string? Finish(string text, Uri page)
    {
        text = Comment().Replace(text, string.Empty);
        text = Image().Replace(text, match => match.Groups["alt"].Value);
        text = Link().Replace(text, match => Resolve(match, page));
        text = BlankRuns().Replace(text.Trim(), "\n\n");
        return text.Length is 0 or > UpdatePlanStore.MaximumNotesCharacters ? null : text;
    }

    // 只留 http／https：相对地址按页面地址解析，其他协议（mailto 等）与无法解析的目标只留链接文字。
    private static string Resolve(Match match, Uri page)
    {
        var text = match.Groups["text"].Value;
        var target = match.Groups["target"].Value;
        if (target.Length == 0) return text;
        try
        {
            var uri = Uri.TryCreate(target, UriKind.Absolute, out var absolute) ? absolute : new Uri(page, target);
            return uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? $"[{text}]({uri.AbsoluteUri})" : text;
        }
        catch (UriFormatException) { return text; }
    }
}
