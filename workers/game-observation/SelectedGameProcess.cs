using System.Diagnostics;

namespace MabiTool.Observation.Worker;

internal sealed class WorkerObservationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
internal sealed class SelectedGameProcess : IDisposable
{
    private readonly Process process;
    public int ProcessId => process.Id;
    public string ImagePath { get; }
    private readonly DateTime startedUtc;
    private SelectedGameProcess(Process process, string imagePath) { this.process = process; ImagePath = imagePath; startedUtc = process.StartTime.ToUniversalTime(); }
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
                string.Equals(game.process.MainModule?.FileName, game.ImagePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
    public void Dispose() => process.Dispose();
}

