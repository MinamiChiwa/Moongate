using System.Text;
using System.Text.RegularExpressions;

namespace MabiTool.Infrastructure;

/// <summary>
/// 调试采集文字详情的清洗：数据根、安装根、用户目录替换为 &lt;data&gt;、&lt;install&gt;、&lt;user&gt;，其余本机绝对路径的目录部分替换为
/// &lt;path&gt;；去除形似令牌或密钥的长串、Bearer 凭据和网络地址；去除控制字符并按单条上限截断。
/// </summary>
public sealed partial class DiagnosticDetailSanitizer
{
    private readonly (string Root, string Name)[] roots;

    public DiagnosticDetailSanitizer(string? dataRoot, string? installRoot, string? userRoot)
    {
        roots = new[] { (dataRoot, "<data>"), (installRoot, "<install>"), (userRoot, "<user>") }
            .Where(x => !string.IsNullOrWhiteSpace(x.Item1) && Path.IsPathFullyQualified(x.Item1!))
            .Select(x => (Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.Item1!)), x.Item2))
            .OrderByDescending(x => x.Item1.Length).ToArray();
    }

    /// <summary>组合异常与附加文字（例如 Worker 输出尾部）并清洗；附加文字保留末尾。</summary>
    public string? Compose(Exception? exception, string? extra)
    {
        var parts = new List<string>();
        if (exception is not null) parts.Add(Truncate(Clean(exception.ToString()), extra is null ? DiagnosticSanitizer.MaximumDetailChars : DiagnosticSanitizer.MaximumDetailChars / 2));
        if (!string.IsNullOrEmpty(extra))
        {
            var cleaned = Clean(extra);
            var room = DiagnosticSanitizer.MaximumDetailChars - parts.Sum(x => x.Length + 1);
            if (cleaned.Length > room) cleaned = cleaned[^Math.Max(0, room)..];
            parts.Add(cleaned);
        }
        var result = string.Join('\n', parts.Where(x => x.Length > 0));
        return result.Length == 0 ? null : result;
    }

    public string Clean(string text)
    {
        foreach (var (root, name) in roots)
        {
            text = text.Replace(root, name, StringComparison.OrdinalIgnoreCase);
            text = text.Replace(root.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase);
        }
        text = AbsolutePath().Replace(text, "<path>\\");
        text = UncPath().Replace(text, "<path>\\");
        return StripSecrets(text);
    }

    /// <summary>与路径无关的清洗；落盘复验时重复执行结果不变。</summary>
    public static string StripSecrets(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) if (!char.IsControl(c) || c is '\n' or '\t') builder.Append(c);
        text = builder.ToString();
        text = Bearer().Replace(text, "Bearer <redacted>");
        text = Secret().Replace(text, "<redacted>");
        text = IPv4().Replace(text, "<address>");
        text = IPv6().Replace(text, "<address>");
        return text;
    }

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit];

    [GeneratedRegex(@"(?<![A-Za-z0-9<])[A-Za-z]:[\\/](?:[^\\/\s:*?""<>|\r\n]+[\\/])*")]
    private static partial Regex AbsolutePath();
    [GeneratedRegex(@"\\\\[^\\/\s]+\\[^\\/\s]+\\(?:[^\\/\s:*?""<>|\r\n]+\\)*")]
    private static partial Regex UncPath();
    [GeneratedRegex(@"(?i)\bBearer\s+\S+")]
    private static partial Regex Bearer();
    [GeneratedRegex(@"(?<![A-Za-z0-9+/_=-])[A-Za-z0-9+/_=-]{32,}(?![A-Za-z0-9+/_=-])")]
    private static partial Regex Secret();
    [GeneratedRegex(@"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?::\d{1,5})?\b")]
    private static partial Regex IPv4();
    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}(?![0-9A-Fa-f:])|(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{1,4}:){1,6}:[0-9A-Fa-f]{0,4}(?![0-9A-Fa-f:])")]
    private static partial Regex IPv6();
}
