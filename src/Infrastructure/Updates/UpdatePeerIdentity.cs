using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.Infrastructure.Updates;

internal sealed record UpdatePeerIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath)
{
    [SupportedOSPlatform("windows")]
    internal static UpdatePeerIdentity Capture(Process process)
    {
        var image = new StringBuilder(32_768);
        var size = image.Capacity;
        // 查询内核记录的映像路径，不依赖尚在初始化的模块列表。
        if (!QueryFullProcessImageNameW(process.SafeHandle, 0, image, ref size) || process.HasExited)
            throw new UpdatePlanException("update.handoffPeerInvalid");
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, image.ToString());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, StringBuilder image, ref int size);
}

/// <summary>保留实际进程句柄，避免把复用的 PID 当作原交接对象。</summary>
[SupportedOSPlatform("windows")]
internal sealed class PinnedUpdatePeer : IDisposable
{
    private readonly Process process;
    private int disposed;
    internal UpdatePeerIdentity Identity { get; }

    internal PinnedUpdatePeer(UpdatePeerIdentity expected)
    {
        if (expected is null || expected.ProcessId <= 0 || expected.StartTimeUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(expected.ExecutablePath) || !Path.IsPathFullyQualified(expected.ExecutablePath))
            throw Invalid();
        try
        {
            process = Process.GetProcessById(expected.ProcessId);
            try
            {
                _ = process.Handle;
                Identity = UpdatePeerIdentity.Capture(process);
                if (Identity.StartTimeUtcTicks != expected.StartTimeUtcTicks ||
                    !Path.GetFullPath(Identity.ExecutablePath).Equals(Path.GetFullPath(expected.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                    throw Invalid("identity");
                VerifyAlive();
            }
            catch { process.Dispose(); throw; }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            var failure = Invalid("open");
            failure.Data["peerOpenError"] = error is Win32Exception native ? native.NativeErrorCode : error.HResult;
            throw failure;
        }
    }

    internal void VerifyPipe(PipeStream pipe, bool server)
    {
        VerifyAlive();
        var success = server ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out id);
        if (!success || id != Identity.ProcessId) throw Invalid("pipe");
        VerifyAlive();
    }

    internal void VerifyAlive()
    {
        if (Volatile.Read(ref disposed) != 0) throw Invalid();
        try { if (process.HasExited) throw Invalid("exited"); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { throw Invalid("alive"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) process.Dispose();
    }

    private static UpdatePlanException Invalid(string check = "arguments")
    {
        var error = new UpdatePlanException("update.handoffPeerInvalid"); error.Data["peerCheck"] = check; return error;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
