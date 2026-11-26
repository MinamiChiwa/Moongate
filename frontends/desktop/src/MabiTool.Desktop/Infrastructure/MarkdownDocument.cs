using System.Text.RegularExpressions;

namespace MabiTool.Desktop.Infrastructure;

public enum MarkdownBlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Quote,
    Rule
}

/// <summary>
/// 行内片段：普通文字、粗体、行内代码或链接。<see cref="Url"/> 只会是 http／https 地址，其余链接解析时只留文字。
/// 同一块里的换行保留为文字中的换行符。
/// </summary>
public sealed record MarkdownInline(string Text, bool Bold = false, bool Code = false, string? Url = null);

/// <summary>
/// 一个块：标题（<see cref="Level"/> 1–6）、段落、列表项（<see cref="Level"/> 为缩进层级，从 0 起；<see cref="Marker"/> 为项目符号或序号）、
/// 引用或分隔线。
/// </summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, IReadOnlyList<MarkdownInline> Inlines, int Level = 0, string? Marker = null);

/// <summary>
/// 更新说明用的轻量 Markdown 解析：标题、无序／有序列表（含缩进嵌套）、引用、分隔线、段落，行内的粗体、行代码与链接。
/// 只做这些，不执行任何内容；未识别的写法按普通文字显示。
/// </summary>
public static partial class MarkdownDocument
{
    /// <summary>块数上限，超出的部分不再解析。</summary>
    public const int MaximumBlocks = 2000;

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<text>.*?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s{0,3}(?:-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^(?<indent>\s*)(?:(?<bullet>[-*+])|(?<number>\d{1,9})[.)])\s+(?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ListLine();

    [GeneratedRegex(@"^\s{0,3}>\s?(?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex QuoteLine();

    [GeneratedRegex(@"\G\[(?<text>[^\]\n]*)\]\((?<url>[^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkAt();

    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var quote = new List<string>();
        (int Level, string Marker, List<string> Lines)? item = null;
        var indents = new List<int>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0) blocks.Add(new(MarkdownBlockKind.Paragraph, Inlines(string.Join("\n", paragraph.Select(line => line.Trim())))));
            paragraph.Clear();
        }
        void FlushQuote()
        {
            if (quote.Count > 0) blocks.Add(new(MarkdownBlockKind.Quote, Inlines(string.Join("\n", quote.Select(line => line.Trim())))));
            quote.Clear();
        }
        void FlushItem()
        {
            if (item is { } open) blocks.Add(new(MarkdownBlockKind.ListItem, Inlines(string.Join("\n", open.Lines.Select(line => line.Trim()))), open.Level, open.Marker));
            item = null;
        }
        void FlushAll() { FlushParagraph(); FlushQuote(); FlushItem(); }

        foreach (var line in lines)
        {
            if (blocks.Count >= MaximumBlocks) break;
            if (line.Trim().Length == 0)
            {
                FlushAll();
                continue;
            }
            if (RuleLine().IsMatch(line))
            {
                FlushAll();
                indents.Clear();
                blocks.Add(new(MarkdownBlockKind.Rule, []));
            }
            else if (HeadingLine().Match(line) is { Success: true } heading)
            {
                FlushAll();
                indents.Clear();
                blocks.Add(new(MarkdownBlockKind.Heading, Inlines(heading.Groups["text"].Value), heading.Groups["hashes"].Length));
            }
            else if (QuoteLine().Match(line) is { Success: true } quoted)
            {
                FlushParagraph(); FlushItem();
                quote.Add(quoted.Groups["text"].Value);
            }
            else if (ListLine().Match(line) is { Success: true } listed)
            {
                FlushParagraph(); FlushQuote(); FlushItem();
                var indent = listed.Groups["indent"].Value.Replace("\t", "    ").Length;
                // 缩进更深就是下一层，回到更浅的缩进就回到那一层。
                while (indents.Count > 0 && indents[^1] > indent) indents.RemoveAt(indents.Count - 1);
                if (indents.Count == 0 || indents[^1] < indent) indents.Add(indent);
                var marker = listed.Groups["bullet"].Success ? "•" : listed.Groups["number"].Value + ".";
                item = (indents.Count - 1, marker, [listed.Groups["text"].Value]);
            }
            else if (item is not null && line.Length > 0 && char.IsWhiteSpace(line[0])) item.Value.Lines.Add(line);
            else
            {
                FlushQuote(); FlushItem();
                indents.Clear();
                paragraph.Add(line);
            }
        }
        FlushAll();
        return blocks;
    }

    /// <summary>行内解析：<c>**粗体**</c>、<c>`代码`</c>、<c>[文字](http 地址)</c>，反斜杠可转义标点。</summary>
    public static IReadOnlyList<MarkdownInline> Inlines(string text) => Inlines(text, bold: false, url: null);

    private static List<MarkdownInline> Inlines(string text, bool bold, string? url)
    {
        var result = new List<MarkdownInline>();
        var buffer = new System.Text.StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            result.Add(new(buffer.ToString(), bold, Url: url));
            buffer.Clear();
        }
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\\' && index + 1 < text.Length && char.IsPunctuation(text[index + 1]))
            {
                buffer.Append(text[++index]);
                continue;
            }
            if (current == '*' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var close = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                if (close > index + 2)
                {
                    Flush();
                    result.AddRange(Inlines(text[(index + 2)..close], bold: true, url));
                    index = close + 1;
                    continue;
                }
            }
            if (current == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index + 1)
                {
                    Flush();
                    result.Add(new(text[(index + 1)..end], bold, Code: true, Url: url));
                    index = end;
                    continue;
                }
            }
            if (current == '[' && url is null && LinkAt().Match(text, index) is { Success: true } link)
            {
                Flush();
                var target = Uri.TryCreate(link.Groups["url"].Value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
                result.AddRange(Inlines(link.Groups["text"].Value, bold, target));
                index += link.Length - 1;
                continue;
            }
            buffer.Append(current);
        }
        Flush();
        return result;
    }
}
