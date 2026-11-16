using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.Platform.Windows;

/// <summary>进程创建时原子加入私有 Job；宿主退出时由内核收回子进程树。</summary>
public sealed class WindowsOwnedChildProcess : IAsyncDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle process;
    private readonly EventWaitHandle wait;
    private readonly RegisteredWaitHandle registration;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private Task? disposal;
    public int Id { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public bool HasExited => exited.Task.IsCompleted;

    private WindowsOwnedChildProcess(SafeFileHandle job, SafeFileHandle process, uint id,
        SafeFileHandle output, SafeFileHandle error)
    {
        this.job = job; this.process = process; Id = checked((int)id);
        StandardOutput = new(new FileStream(output, FileAccess.Read, 4096, false), new UTF8Encoding(false));
        StandardError = new(new FileStream(error, FileAccess.Read, 4096, false), new UTF8Encoding(false));
        wait = new(false, EventResetMode.ManualReset);
        wait.SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false);
        registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
        {
            if (GetExitCodeProcess(this.process, out var code)) exited.TrySetResult(unchecked((int)code));
            else exited.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
        }, null, Timeout.Infinite, true);
    }

    public static WindowsOwnedChildProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("私有进程 Job 需要 Windows");
        if (!Path.IsPathFullyQualified(executable) || arguments.Any(value => value is null || value.Contains('\0')))
            throw new ArgumentException("子进程需要绝对路径和有效参数");
        SafeFileHandle? job = null, outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, input = null;
        nint attributes = 0, handles = 0, jobs = 0;
        ProcessInformation created = default;
        var assigned = false;
        var attributesInitialized = false;
        try
        {
            job = CreateJobObjectW(0, null); Check(!job.IsInvalid);
            var limits = new ExtendedLimitInformation { Basic = new() { LimitFlags = 0x2000 } };
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()));
            var security = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            Check(CreatePipe(out outputRead, out outputWrite, ref security, 0));
            Check(CreatePipe(out errorRead, out errorWrite, ref security, 0));
            Check(SetHandleInformation(outputRead, 1, 0)); Check(SetHandleInformation(errorRead, 1, 0));
            input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, 0); Check(!input.IsInvalid);
            nuint bytes = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((nint)bytes));
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes));
            attributesInitialized = true;
            handles = Marshal.AllocHGlobal(3 * nint.Size); jobs = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, nint.Size, outputWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * nint.Size, errorWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * nint.Size), 0, 0));
            Check(UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobs, (nuint)nint.Size, 0, 0));
            var startup = new StartupInfoEx { Info = new() { Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                Flags = 0x100, Input = input.DangerousGetHandle(), Output = outputWrite.DangerousGetHandle(),
                Error = errorWrite.DangerousGetHandle() }, Attributes = attributes };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
            Check(CreateProcessW(executable, command, 0, 0, true, 0x08080000, 0,
                Path.GetDirectoryName(executable), ref startup, out created));
            var process = new SafeFileHandle(created.Process, true); created.Process = 0;
            WindowsOwnedChildProcess result;
            try { result = new(job, process, created.Id, outputRead, errorRead); }
            catch { TerminateJobObject(job, 1); process.Dispose(); throw; }
            assigned = true; return result;
        }
        finally
        {
            if (created.Thread != 0) CloseHandle(created.Thread);
            if (created.Process != 0) CloseHandle(created.Process);
            if (attributes != 0)
            {
                if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (handles != 0) Marshal.FreeHGlobal(handles);
            if (jobs != 0) Marshal.FreeHGlobal(jobs);
            outputWrite?.Dispose(); errorWrite?.Dispose(); input?.Dispose();
            if (!assigned) { job?.Dispose(); outputRead?.Dispose(); errorRead?.Dispose(); }
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken ct = default) => exited.Task.WaitAsync(ct);
    public void Terminate()
    {
        if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        try { Terminate(); await exited.Task.ConfigureAwait(false); }
        finally
        {
            registration.Unregister(null); wait.Dispose();
            StandardOutput.Dispose(); StandardError.Dispose(); process.Dispose(); job.Dispose();
        }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public uint Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic; public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedBytes; public nint ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint Id, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref ExtendedLimitInformation value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, ref SecurityAttributes security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint attributes, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
