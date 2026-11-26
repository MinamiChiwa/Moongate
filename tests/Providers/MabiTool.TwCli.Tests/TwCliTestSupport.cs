using System.Text;
using MabiTool.Providers.TwCli;

namespace MabiTool.TwCli.Tests;

/// <summary>测试用临时目录，位于仓库 artifacts/tmp（生成物目录），用后删除。</summary>
internal sealed class TwCliWorkspace : IDisposable
{
    public string Root { get; }
    public TwCliWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
        Root = Path.Combine(directory!.FullName, "artifacts", "tmp", "twcli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }
    public string PathFor(params string[] parts) => Path.Combine([Root, .. parts]);
    public void Dispose() => Directory.Delete(Root, true);
}

/// <summary>按指令名返回预设响应的替身命令行：记录每次调用并统计并发数，支持延迟与取消。</summary>
internal sealed class ScriptedRunner : ITwCliRunner
{
    private int running, maximum;
    public List<string[]> Calls { get; } = [];
    public List<string> Directories { get; } = [];
    public TimeSpan Delay { get; set; }
    public Func<string, string[], TwCliRun> Respond { get; set; } = (_, _) => new(0, "{}", "", false, TimeSpan.FromMilliseconds(5));
    public int MaximumConcurrent => maximum;
    public string[] Commands { get { lock (Calls) return Calls.Select(x => x[0]).ToArray(); } }

    public async Task<TwCliRun> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref running);
        int seen;
        do { seen = maximum; } while (current > seen && Interlocked.CompareExchange(ref maximum, current, seen) != seen);
        try
        {
            lock (Calls) { Calls.Add([.. arguments]); Directories.Add(workingDirectory); }
            if (Delay > TimeSpan.Zero)
            {
                try { await Task.Delay(Delay, cancellationToken); }
                catch (OperationCanceledException) { return new(-1, "", "", true, Delay); }
            }
            return Respond(arguments[0], [.. arguments]);
        }
        finally { Interlocked.Decrement(ref running); }
    }

    public static TwCliRun Ok(string json) => new(0, json, "", false, TimeSpan.FromMilliseconds(5));

    public static string Catalog(bool loading, params string[] commands) =>
        "{\"commands\":[" + string.Join(",", commands.Select(c => "{\"Command\":\"" + c + "\",\"Description\":\"d\",\"BodyExample\":\"\",\"OutputExample\":\"o\"}")) + "]" +
        (loading ? ",\"loading\":true" : "") + "}";
}

internal static class FakeCliScript
{
    /// <summary>写出一个批处理替身：按 &lt;指令&gt;.out/.err/.code 返回预设输出，并把原始参数追加到 calls.log；&lt;指令&gt;.delay 存在时先等待几秒。</summary>
    public static string Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fake-cli.cmd");
        File.WriteAllText(path, string.Join("\r\n", [
            "@echo off", "setlocal", "set \"D=%~dp0\"", ">>\"%D%calls.log\" echo %*",
            "if exist \"%D%%1.delay\" ping -n 8 127.0.0.1 >nul",
            "if exist \"%D%%1.out\" type \"%D%%1.out\"",
            "if exist \"%D%%1.err\" type \"%D%%1.err\" 1>&2",
            "set CODE=0", "if exist \"%D%%1.code\" set /p CODE=<\"%D%%1.code\"", "exit /b %CODE%", ""]), new UTF8Encoding(false));
        return path;
    }
}
