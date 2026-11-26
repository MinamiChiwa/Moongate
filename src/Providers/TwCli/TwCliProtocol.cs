using System.Text;
using System.Text.Json;

namespace MabiTool.Providers.TwCli;

/// <summary>命令行进程的一次执行结果；<see cref="Interrupted"/> 为真表示调用方取消或紧急停止中断了进程。</summary>
public sealed record TwCliRun(int ExitCode, string Stdout, string Stderr, bool Interrupted, TimeSpan Elapsed);

public enum TwCliResultKind
{
    /// <summary>退出码 0，响应是数据或已受理。</summary>
    Ok,
    /// <summary>退出码 0 但游戏拒绝（顶层 error、status 为 rejected/invalid_body、body.error）。</summary>
    Rejected,
    /// <summary>退出码 5：连接器没有连上游戏，<see cref="TwCliResult.Reason"/> 为 game_off 或 option_off。</summary>
    NotConnected,
    /// <summary>退出码 4：未知指令，目录需要刷新。</summary>
    UnknownCommand,
    /// <summary>退出码 2：用法或适配器本地错误。</summary>
    Usage,
    /// <summary>退出码 3：取消。</summary>
    Cancelled,
    /// <summary>进程以其他退出码结束，或无法启动。</summary>
    ProcessFailure,
    /// <summary>退出码 0 但标准输出无法解析。</summary>
    Unparseable,
    /// <summary>调用方取消或紧急停止中断了进程，游戏动作状态未知。</summary>
    Interrupted,
    /// <summary>没有可调用的命令行工具（本地判定，没有启动进程）。</summary>
    Unavailable
}

public sealed record TwCliResult(string Command, int ExitCode, TwCliResultKind Kind, string? Error, string? Reason,
    string? Message, JsonElement? Root, string Stdout, string Stderr, TimeSpan Elapsed)
{
    public bool IsOk => Kind == TwCliResultKind.Ok;

    /// <summary>数据主体：响应带 <c>status</c> 与 <c>body</c> 包装时取 body，否则就是根节点。</summary>
    public JsonElement? Body
    {
        get
        {
            if (Root is not { } root) return null;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out _) && root.TryGetProperty("body", out var body) ? body : root;
        }
    }

    public static TwCliResult Local(string command, TwCliResultKind kind, string error, string? message = null) =>
        new(command, -1, kind, error, null, message, null, string.Empty, string.Empty, TimeSpan.Zero);
}

/// <summary>指令 body：JSON 先序列化为完整字符串，纯文本直接取文本；最终以 UTF-8 的 <c>base64:</c> 单个参数传给命令行。</summary>
public readonly record struct TwCliBody(string Text, bool IsRaw = false)
{
    public static TwCliBody Json<T>(T value) => new(JsonSerializer.Serialize(value, new JsonSerializerOptions
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    public static TwCliBody Plain(string text) => new(text);
    public string Argument => IsRaw || Text.StartsWith("base64:", StringComparison.Ordinal)
        ? Text : "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Text));
}

public static class TwCliResponse
{
    public const int ExitUsage = 2, ExitCancelled = 3, ExitUnknownCommand = 4, ExitNotConnected = 5;

    public static TwCliResult Classify(string command, TwCliRun run)
    {
        TwCliResult Make(TwCliResultKind kind, string? error = null, string? reason = null, string? message = null, JsonElement? root = null) =>
            new(command, run.ExitCode, kind, error, reason, message, root, run.Stdout, run.Stderr, run.Elapsed);
        if (run.Interrupted) return Make(TwCliResultKind.Interrupted, "gameCli.interrupted");
        var root = TryParse(run.Stdout) ?? TryParse(run.Stderr);
        switch (run.ExitCode)
        {
            case 0: break;
            case ExitNotConnected:
                return Make(TwCliResultKind.NotConnected, "gameCli.notConnected", NotConnectedReason(root, run), Text(root, "message"), root);
            case ExitUnknownCommand: return Make(TwCliResultKind.UnknownCommand, "gameCli.unknownCommand", null, FirstLine(run.Stderr), root);
            case ExitUsage: return Make(TwCliResultKind.Usage, "gameCli.usage", null, FirstLine(run.Stderr), root);
            case ExitCancelled: return Make(TwCliResultKind.Cancelled, "gameCli.cancelled", null, FirstLine(run.Stderr), root);
            default: return Make(TwCliResultKind.ProcessFailure, "gameCli.failed", null, FirstLine(run.Stderr), root);
        }
        var stdout = run.Stdout.Trim();
        if (stdout.Length == 0) return Make(TwCliResultKind.Ok);
        if (TryParse(run.Stdout) is not { } parsed) return Make(TwCliResultKind.Unparseable, "gameCli.unparseable", null, FirstLine(stdout));
        var rejection = Rejection(parsed);
        return rejection is null ? Make(TwCliResultKind.Ok, root: parsed)
            : Make(TwCliResultKind.Rejected, rejection.Value.Error, null, rejection.Value.Message, parsed);
    }

    /// <summary>退出码 0 的业务拒绝：顶层 error、status 为 rejected/invalid_body、body.error。数组根与数据对象没有拒绝。</summary>
    private static (string Error, string? Message)? Rejection(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Text(root, "error") is { Length: > 0 } top) return (top, Text(root, "message"));
        var status = Text(root, "status");
        JsonElement? body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? b : null;
        if (body is { } inner && Text(inner, "error") is { Length: > 0 } nested) return (nested, Text(inner, "message"));
        if (status is "rejected" or "invalid_body") return (status, body is { } details ? Text(details, "message") : null);
        return null;
    }

    private static string? NotConnectedReason(JsonElement? root, TwCliRun run)
    {
        if (root is { } value)
        {
            if (Text(value, "reason") is { Length: > 0 } direct) return direct;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("body", out var body) && Text(body, "reason") is { Length: > 0 } nested) return nested;
        }
        foreach (var text in new[] { run.Stdout, run.Stderr })
        {
            if (text.Contains("option_off", StringComparison.Ordinal)) return "option_off";
            if (text.Contains("game_off", StringComparison.Ordinal)) return "game_off";
        }
        return null;
    }

    public static JsonElement? TryParse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '[')) return null;
        try { using var document = JsonDocument.Parse(trimmed); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    internal static string? Text(JsonElement? value, string name) => value is { ValueKind: JsonValueKind.Object } row &&
        row.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static string? FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? null : line.Length > 256 ? line[..256] : line;
    }
}
