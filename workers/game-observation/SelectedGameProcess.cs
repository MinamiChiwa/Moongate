using System.Diagnostics;

namespace MabiTool.Observation.Worker;

internal sealed class WorkerObservationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// 归入 observation.captureFailed 的捕获故障及其细分原因（固定码，不含地址、路径与载荷），随错误帧的 detail 字段送到宿主，
/// 写入 observation.worker.faulted 与 observation.capture.ended 的 kind 字段。
/// </summary>
internal sealed class CaptureFaultException(string kind, Exception? inner = null) : IOException(kind, inner)
{
    public string Kind { get; } = kind;

    /// <summary>未归类异常按类型给出的细分码：前缀为发生位置（capture 捕获循环、writer 写出循环）。</summary>
    public static string Classify(string area, Exception error) => error switch
    {
        CaptureFaultException fault => fault.Kind,
        ObjectDisposedException => area + ".disposed",
        InvalidOperationException => area + ".invalidOperation",
        ArgumentException => area + ".argument",
        IndexOutOfRangeException => area + ".indexOutOfRange",
        NullReferenceException => area + ".nullReference",
        OverflowException => area + ".overflow",
        InvalidDataException => area + ".invalidData",
        KeyNotFoundException => area + ".keyNotFound",
        OutOfMemoryException => area + ".outOfMemory",
        System.Runtime.InteropServices.SEHException or System.Runtime.InteropServices.ExternalException => area + ".native",
        IOException => area + ".io",
        _ => area + ".unclassified"
    };
}
internal sealed class SelectedGameProcess : IDisposable
{
    private readonly Process process;
    public int ProcessId => process.Id;
    /// <summary>客户端主程序的最终路径：联接、符号链接与挂载点已解析（游戏迁到别处再挂回原路径时，客户端文件按实际位置读取与校验）。</summary>
    public string ImagePath { get; }
    /// <summary>进程报告的映像路径，用于核对仍是同一进程。</summary>
    private readonly string reportedPath;
    private readonly DateTime startedUtc;
    private SelectedGameProcess(Process process, string imagePath)
    {
        this.process = process; reportedPath = imagePath; ImagePath = FinalImagePath(imagePath); startedUtc = process.StartTime.ToUniversalTime();
    }
    /// <summary>解析为最终路径；解析不了时保留原路径（其后的链接检查会拒绝含链接的路径）。</summary>
    internal static string FinalImagePath(string imagePath) =>
        MabiTool.Providers.TwObservation.Combat.TwClientFingerprint.ResolveFinalPath(imagePath) ?? imagePath;
    public static SelectedGameProcess Open(int pid, DateTimeOffset startedAt)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            _ = process.Handle;
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startedAt.UtcTicks)
                throw new WorkerObservationException("input.bindingChanged");
            var image = process.MainModule?.FileName ?? throw new WorkerObservationException("input.bindingChanged");
            return new(process, image);
        }
        catch { process?.Dispose(); throw; }
    }
    public static bool IsRunning(SelectedGameProcess game)
    {
        try
        {
            game.process.Refresh();
            return !game.process.HasExited && game.process.StartTime.ToUniversalTime() == game.startedUtc &&
                string.Equals(game.process.MainModule?.FileName, game.reportedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
    public void Dispose() => process.Dispose();
}

