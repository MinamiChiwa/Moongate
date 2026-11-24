using System.Runtime.InteropServices;
using System.Text;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 常驻捕获的读取方式。<see cref="Immediate"/>：Npcap 即时模式，内核缓冲一有包即触发读事件，捕获线程等待各句柄的读事件；
/// <see cref="MinToCopy"/>：内核缓冲累积到最小拷贝量才触发读事件，另以短超时兜底；<see cref="Poll"/>：没有读事件或即时模式的 Npcap 上按固定周期非阻塞读取。
/// </summary>
internal enum CaptureReadMode { Poll, Immediate, MinToCopy }

/// <param name="Mode">读取方式。</param>
/// <param name="BufferBytes">每个句柄的内核缓冲字节数。</param>
/// <param name="MinToCopyBytes">MinToCopy 方式的最小拷贝量。</param>
/// <param name="TimeoutMs">MinToCopy 方式的等待超时、Poll 方式的轮询间隔。</param>
/// <param name="CoalesceMs">Immediate 方式的合批间隔：距上次唤醒不足该间隔又有包时，先等这一间隔再读（持续高流量时按间隔成批读取，
/// 等待由高精度计时器唤醒）；0 为不合批。</param>
internal sealed record CaptureReadConfig(CaptureReadMode Mode, int BufferBytes, int MinToCopyBytes = 0, int TimeoutMs = 10, int CoalesceMs = 0)
{
    /// <summary>
    /// 常驻捕获采用的读取方式：有包即由读事件唤醒，相邻唤醒不足 4 ms 又有包时先等 4 ms 成批读取（每秒至多约 250 次唤醒）；
    /// 本机回环实测空闲时每秒只唤醒 2 次，低于轮询的每秒约 64 次读取。
    /// </summary>
    public static CaptureReadConfig Resident { get; } = new(CaptureReadMode.Immediate, NativeCaptureBoundary.DeviceBufferBytes, CoalesceMs: 4);
    /// <summary>不支持读事件时的回退：非阻塞读取、每轮间隔 10 ms。</summary>
    public CaptureReadConfig Fallback => this with { Mode = CaptureReadMode.Poll, TimeoutMs = 10, CoalesceMs = 0 };
    /// <summary>诊断计数中的读取方式；<paramref name="fallback"/> 为计划的读事件方式因 Npcap 不支持而回退。</summary>
    public MabiTool.Contracts.CaptureReadCounters Counters(bool fallback) => new(Mode switch
    {
        CaptureReadMode.Immediate => "immediate",
        CaptureReadMode.MinToCopy => "minToCopy",
        _ => "poll"
    }, fallback, Mode == CaptureReadMode.Immediate ? CoalesceMs : 0, Mode == CaptureReadMode.Immediate ? 0 : TimeoutMs, BufferBytes);
}

internal static partial class CaptureChannels
{
    /// <summary>
    /// 按读取方式打开一个非阻塞句柄并设好过滤器。读事件取不到（旧版 Npcap 没有 pcap_getevent、即时模式或最小拷贝量设置失败）时
    /// 读事件为 0，调用方改用轮询。打不开时抛出 <see cref="IOException"/>；链路类型不受支持（原因 link:值）或过滤器设置失败（filter）时
    /// 关闭句柄并给出原因，<paramref name="link"/> 为读到的链路类型。
    /// </summary>
    internal static (nint Handle, nint Event, string? Issue) OpenReadHandle(string name, CaptureReadConfig config, string filter, StringBuilder error, out int link)
    {
        var immediate = false;
        var handle = OpenConfigured(name, error, config.BufferBytes, config.Mode == CaptureReadMode.Immediate
            ? value => { var result = pcap_set_immediate_mode(value, 1); immediate = result == 0; return result; } : null);
        link = -1;
        try
        {
            link = pcap_datalink(handle);
            string? issue = !Providers.TwObservation.GamePacket.SupportsLink(link) ? "link:" + link.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : !Apply(handle, filter) || pcap_setnonblock(handle, 1, error) != 0 ? "filter" : null;
            if (issue is not null) { pcap_close(handle); return (0, 0, issue); }
            var wait = config.Mode switch
            {
                CaptureReadMode.Immediate => immediate,
                CaptureReadMode.MinToCopy => TrySet(() => pcap_setmintocopy(handle, config.MinToCopyBytes) == 0),
                _ => false
            };
            return (handle, wait ? ReadEvent(handle) : 0, null);
        }
        catch { pcap_close(handle); throw; }
    }
    private static bool TrySet(Func<bool> set)
    {
        try { return set(); }
        catch (EntryPointNotFoundException) { return false; }
    }
    private static nint ReadEvent(nint handle)
    {
        try { return pcap_getevent(handle); }
        catch (EntryPointNotFoundException) { return 0; }
    }
    /// <summary>
    /// 等待任一读事件或取消；返回触发的序号、超时为 -1。句柄至多 63 个（常驻捕获至多 32 张设备，另加取消事件）。
    /// </summary>
    internal static int WaitAny(nint[] events, int count, int timeoutMs)
    {
        if (count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(count));
        var result = WaitForMultipleObjects((uint)count, events, false, (uint)Math.Max(0, timeoutMs));
        return result < count ? (int)result : result is >= 0x80 and < 0x80 + 64 ? (int)(result - 0x80) : result == 0x102 ? -1
            : throw new IOException("等待抓包读事件失败：" + Marshal.GetLastPInvokeError());
    }
    /// <summary>读一个包：1 读到、0 暂无、其他为读取失败。</summary>
    internal static int ReadNext(nint handle, out nint header, out nint packet) => pcap_next_ex(handle, out header, out packet);
    internal static uint Drops(nint handle) => pcap_stats(handle, out var stats) == 0 ? unchecked(stats.Dropped + stats.InterfaceDropped) : 0;
    internal static void CloseHandle(nint handle) => pcap_close(handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_set_immediate_mode(nint handle, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_setmintocopy(nint handle, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint pcap_getevent(nint handle);
    [DllImport("kernel32", SetLastError = true)] private static extern uint WaitForMultipleObjects(uint count, nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);
}
