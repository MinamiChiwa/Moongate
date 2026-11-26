using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MabiTool.Providers.TwCli;

public interface ITwCliRunner
{
    /// <summary>启动一次命令行进程并等待结束；取消时杀掉进程树并返回 <see cref="TwCliRun.Interrupted"/>。</summary>
    Task<TwCliRun> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>参数数组启动、隐藏控制台、UTF-8 捕获 stdout 与 stderr；不设超时，输出各限 8 MiB。</summary>
public sealed class TwCliProcessRunner : ITwCliRunner
{
    public const int MaximumOutputChars = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false);

    public async Task<TwCliRun> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        TimeSpan Elapsed() => Stopwatch.GetElapsedTime(started);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (cancellationToken.IsCancellationRequested) return new(-1, string.Empty, string.Empty, true, TimeSpan.Zero);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(-1, string.Empty, "启动命令行工具失败：" + error.Message, false, Elapsed());
        }
        try { process.StandardInput.Close(); } catch (IOException) { }
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        var interrupted = false;
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            interrupted = true;
            try { process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
        }
        var outText = await stdout.ConfigureAwait(false);
        var errText = await stderr.ConfigureAwait(false);
        return new(interrupted ? -1 : process.ExitCode, outText, errText, interrupted, Elapsed());
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                if (builder.Length < MaximumOutputChars) builder.Append(buffer, 0, Math.Min(read, MaximumOutputChars - builder.Length));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        return builder.ToString();
    }
}
