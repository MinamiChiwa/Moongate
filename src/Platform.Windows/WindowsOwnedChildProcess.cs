using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.Platform.Windows;

/// <summary>
/// 子进程的可选限制。<see cref="Environment"/> 为空时继承宿主环境，否则只传入这些变量；
/// <see cref="JobMemoryBytes"/> 限制整个进程树的提交内存，<see cref="CpuRatePercent"/> 以整机 CPU 百分比硬性封顶；
/// <see cref="RestrictUserInterface"/> 禁止使用 Job 外窗口的句柄、读写剪贴板、改系统参数与显示设置、注销关机。
/// </summary>
public sealed record ChildProcessOptions(
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool RedirectInput = false,
    long? JobMemoryBytes = null,
    int? CpuRatePercent = null,
    int? ActiveProcessLimit = null,
    bool RestrictUserInterface = false);

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
    /// <summary>只在 <see cref="ChildProcessOptions.RedirectInput"/> 为真时存在。</summary>
    public Stream? StandardInput { get; }
    public bool HasExited => exited.Task.IsCompleted;

    /// <summary>Job 内进程树曾经提交的内存峰值；读取失败时为空。</summary>
    public long? PeakJobMemoryBytes
    {
        get
        {
            var value = new ExtendedLimitInformation();
            return QueryInformationJobObject(job, 9, ref value, (uint)Marshal.SizeOf<ExtendedLimitInformation>(), 0)
                ? (long)value.PeakJobMemory : null;
        }
    }

    private WindowsOwnedChildProcess(SafeFileHandle job, SafeFileHandle process, uint id,
        SafeFileHandle output, SafeFileHandle error, SafeFileHandle? input)
    {
        this.job = job; this.process = process; Id = checked((int)id);
        StandardOutput = new(new FileStream(output, FileAccess.Read, 4096, false), new UTF8Encoding(false));
        StandardError = new(new FileStream(error, FileAccess.Read, 4096, false), new UTF8Encoding(false));
        if (input is not null) StandardInput = new FileStream(input, FileAccess.Write, 4096, false);
        wait = new(false, EventResetMode.ManualReset);
        wait.SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false);
        registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
        {
            if (GetExitCodeProcess(this.process, out var code)) exited.TrySetResult(unchecked((int)code));
            else exited.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
        }, null, Timeout.Infinite, true);
    }

    public static WindowsOwnedChildProcess Start(string executable, IReadOnlyList<string> arguments) =>
        Start(executable, arguments, new ChildProcessOptions());

    public static WindowsOwnedChildProcess Start(string executable, IReadOnlyList<string> arguments, ChildProcessOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("私有进程 Job 需要 Windows");
        if (!Path.IsPathFullyQualified(executable) || arguments.Any(value => value is null || value.Contains('\0')))
            throw new ArgumentException("子进程需要绝对路径和有效参数");
        if (options.WorkingDirectory is { } directory && !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("子进程工作目录必须是绝对路径");
        if (options.Environment?.Any(pair => pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0')) == true)
            throw new ArgumentException("子进程环境变量无效");
        SafeFileHandle? job = null, outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, input = null, inputWrite = null;
        nint attributes = 0, handles = 0, jobs = 0, environment = 0;
        ProcessInformation created = default;
        var assigned = false;
        var attributesInitialized = false;
        try
        {
            job = CreateJobObjectW(0, null); Check(!job.IsInvalid);
            // 0x2000：Job 句柄关闭时结束全部进程；0x400：未处理异常直接结束，不弹出系统错误报告窗口。
            var flags = 0x2000u | 0x400u;
            if (options.JobMemoryBytes is > 0) flags |= 0x200;
            if (options.ActiveProcessLimit is > 0) flags |= 0x8;
            var limits = new ExtendedLimitInformation
            {
                Basic = new() { LimitFlags = flags, ActiveProcessLimit = (uint)(options.ActiveProcessLimit ?? 0) },
                JobMemory = (nuint)(options.JobMemoryBytes ?? 0)
            };
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()));
            if (options.CpuRatePercent is { } percent)
            {
                if (percent is < 1 or > 100) throw new ArgumentException("CPU 上限须在 1–100 之间");
                var rate = new CpuRateControl { Flags = 0x1 | 0x4, Rate = (uint)(percent * 100) };
                Check(SetInformationJobObject(job, 15, ref rate, (uint)Marshal.SizeOf<CpuRateControl>()));
            }
            if (options.RestrictUserInterface)
            {
                var restrictions = new UiRestrictions { Flags = 0xFF };
                Check(SetInformationJobObject(job, 4, ref restrictions, (uint)Marshal.SizeOf<UiRestrictions>()));
            }
            var security = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            Check(CreatePipe(out outputRead, out outputWrite, ref security, 0));
            Check(CreatePipe(out errorRead, out errorWrite, ref security, 0));
            Check(SetHandleInformation(outputRead, 1, 0)); Check(SetHandleInformation(errorRead, 1, 0));
            if (options.RedirectInput)
            {
                Check(CreatePipe(out input, out inputWrite, ref security, 0));
                Check(SetHandleInformation(inputWrite, 1, 0));
            }
            else { input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, 0); Check(!input.IsInvalid); }
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
            if (options.Environment is { } variables)
            {
                // CREATE_UNICODE_ENVIRONMENT：环境块按名称排序，以两个 NUL 结束。
                var block = string.Concat(variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value + "\0")) + "\0";
                environment = Marshal.StringToHGlobalUni(block);
            }
            Check(CreateProcessW(executable, command, 0, 0, true, 0x08080000 | (environment != 0 ? 0x400u : 0), environment,
                options.WorkingDirectory ?? Path.GetDirectoryName(executable), ref startup, out created));
            var process = new SafeFileHandle(created.Process, true); created.Process = 0;
            WindowsOwnedChildProcess result;
            try { result = new(job, process, created.Id, outputRead, errorRead, inputWrite); }
            catch { TerminateJobObject(job, 1); process.Dispose(); throw; }
            assigned = true; return result;
        }
        finally
        {
            if (environment != 0) Marshal.FreeHGlobal(environment);
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
            if (!assigned) { job?.Dispose(); outputRead?.Dispose(); errorRead?.Dispose(); inputWrite?.Dispose(); }
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
            try { StandardInput?.Dispose(); } catch (IOException) { }
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
    private struct CpuRateControl { public uint Flags, Rate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UiRestrictions { public uint Flags; }
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
    private static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref CpuRateControl value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref UiRestrictions value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int information, ref ExtendedLimitInformation value, uint size, nint returned);
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
