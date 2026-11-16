// 来源：MinamiChiwa/MabiSync，固定提交 2cb69b5ddec8de4fe9d20ec98bbb410ac015e671；捕获迁至隔离进程。
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using MabiTool.Infrastructure.Workers;
using MabiTool.Providers.TwObservation;

namespace MabiTool.Observation.Worker;

public sealed record CaptureDevice(string Name, string Description, string[] Addresses)
{
    public bool IsLoopback { get; init; }
    public string Label => $"{Description} · {string.Join(", ", Addresses)}";
}
public sealed record CaptureConnection(string LocalAddress, ushort LocalPort, string RemoteAddress, ushort RemotePort)
{
    public string Label => $"{LocalAddress}:{LocalPort} ↔ {RemoteAddress}:{RemotePort}";
    public string Filter => $"tcp and ((src host {LocalAddress} and src port {LocalPort} and dst host {RemoteAddress} and dst port {RemotePort}) or (src host {RemoteAddress} and src port {RemotePort} and dst host {LocalAddress} and dst port {LocalPort}))";
}
public sealed record ChannelCheck(long Packets, double ElapsedSeconds, string Status);

/// <summary>Optional, read-only Npcap diagnostics. Never saves raw packets or transmits traffic.</summary>
internal static partial class CaptureChannels
{
    private const string Library = "MabiTool.OptionalNpcap";
    static CaptureChannels()
    {
        NativeLibrary.SetDllImportResolver(typeof(CaptureChannels).Assembly, (name, _, _) => name == Library
            ? LoadLibraryEx(NpcapInstallation.LibraryPath, 0, 0x100 | 0x800) : 0);
    }
    /// <summary>
    /// 列出网卡。网卡查询、手动调试与捕获都先经过这里：系统目录下没有 Npcap 时以 observation.npcapMissing 结束，
    /// 与已安装但加载失败（observation.npcapUnavailable）区分。
    /// </summary>
    public static IReadOnlyList<CaptureDevice> Devices()
    {
        if (!File.Exists(NpcapInstallation.LibraryPath)) throw new WorkerObservationException("observation.npcapMissing");
        var error = new StringBuilder(256);
        if (pcap_findalldevs(out var head, error) != 0) throw new IOException(error.ToString());
        var devices = new List<CaptureDevice>();
        try
        {
            for (var next = head; next != 0;)
            {
                var item = Marshal.PtrToStructure<Device>(next); var addresses = new List<string>();
                for (var addr = item.Addresses; addr != 0;)
                {
                    var row = Marshal.PtrToStructure<Address>(addr);
                    if (row.Sockaddr != 0 && Marshal.ReadInt16(row.Sockaddr) == 2)
                    { var bytes = new byte[4]; Marshal.Copy(row.Sockaddr + 4, bytes, 0, 4); addresses.Add(new IPAddress(bytes).ToString()); }
                    addr = row.Next;
                }
                devices.Add(new(Marshal.PtrToStringUTF8(item.Name) ?? "", Marshal.PtrToStringUTF8(item.Description) ?? "网卡", addresses.ToArray()) { IsLoopback = (item.Flags & 1) != 0 });
                next = item.Next;
            }
        }
        finally { pcap_freealldevs(head); }
        return devices;
    }
    /// <summary>
    /// 进程拥有的 TCP 连接。<paramref name="transientStates"/> 为真时连同 SYN_SENT 到 CLOSE_WAIT（3–8）一起列出：
    /// 实采中游戏的回环连接在连接表里反复在 ESTABLISHED 与 FIN_WAIT1 之间切换，新连接首次出现时可能就是 FIN_WAIT1
    /// （2026-11-15 concert 样本：首次列出为 SYN-ACK 后 +2.47 s 的 FIN_WAIT1，+3.67 s 才是 ESTABLISHED），
    /// 只认 ESTABLISHED 会让新连接的确认推迟到离 5 秒预缓冲窗口只剩约 1.3 s。归属仍按进程 ID 判定。
    /// </summary>
    public static IReadOnlyList<CaptureConnection> Connections(int processId, bool transientStates = false) =>
        Table().Where(row => row.ProcessId == processId && (transientStates ? row.State is >= 3 and <= 8 : row.State == 5))
            .Select(row => row.Connection).ToList();

    /// <summary>IPv4 TCP 连接表的全部行（四元组、所属进程 ID、状态），用于确认预缓冲四元组的归属。</summary>
    internal static IReadOnlyList<(CaptureConnection Connection, int ProcessId, int State)> Table()
    {
        uint size = 0; var result = GetExtendedTcpTable(0, ref size, false, 2, 5, 0);
        if (result is not (0 or 122)) throw new IOException("无法读取游戏连接列表：" + result);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var memory = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                result = GetExtendedTcpTable(memory, ref size, false, 2, 5, 0);
                if (result == 122) continue;
                if (result != 0) throw new IOException("无法读取游戏连接列表：" + result);
                var count = Marshal.ReadInt32(memory);
                if (count < 0 || 4L + count * 24L > size) throw new IOException("游戏连接列表长度异常。");
                var rows = new List<(CaptureConnection, int, int)>();
                for (var i = 0; i < count; i++)
                {
                    var at = memory + 4 + i * 24;
                    var local = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(at + 4))).ToString();
                    var remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(at + 12))).ToString();
                    var localPort = (ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(at + 8));
                    var remotePort = (ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(at + 16));
                    if (localPort != 0 && remotePort != 0) rows.Add((new(local, localPort, remote, remotePort), Marshal.ReadInt32(at + 20), Marshal.ReadInt32(at)));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new IOException("连接正在变化，请刷新后重试。");
    }
    public static Task CheckAsync(SelectedGameProcess game, CaptureDevice device, CaptureConnection connection, IProgress<ChannelCheck> progress, CancellationToken token) => Task.Run(() =>
    {
        if (!SelectedGameProcess.IsRunning(game) || !Connections(game.ProcessId).Contains(connection)) throw new IOException("所选游戏或连接已变化，请重新选择。");
        if (!NativeCaptureBoundary.MatchesDevice(device, connection)) throw new IOException("所选网卡与游戏连接不匹配，请换一张网卡后重试。");
        var error = new StringBuilder(256);
        var handle = OpenConfigured(device.Name, error);
        if (handle == 0) throw new IOException("Npcap 无法打开网卡：" + error);
        try
        {
            if (pcap_compile(handle, out var filter, connection.Filter, 1, uint.MaxValue) != 0) throw new IOException("无法建立游戏连接过滤器。");
            try { if (pcap_setfilter(handle, ref filter) != 0) throw new IOException("无法应用游戏连接过滤器。"); }
            finally { pcap_freecode(ref filter); }
            if (pcap_setnonblock(handle, 1, error) != 0) throw new IOException("Npcap 无法启动抓包，请检查安装或尝试其他网卡。");
            var timer = System.Diagnostics.Stopwatch.StartNew(); long count = 0; double last = -1;
            while (!token.IsCancellationRequested && timer.Elapsed.TotalSeconds < 30)
            {
                for (var i = 0; i < 100; i++)
                {
                    var result = pcap_next_ex(handle, out _, out _);
                    if (result == 0) break;
                    if (result != 1) throw new IOException("Npcap 读取中断。");
                    count++;
                }
                if (timer.Elapsed.TotalSeconds - last >= .5)
                {
                    if (!Connections(game.ProcessId).Contains(connection)) { progress.Report(new(count, timer.Elapsed.TotalSeconds, "游戏连接已变化，已停止校对。")); return; }
                    progress.Report(new(count, timer.Elapsed.TotalSeconds, count == 0 ? "等待所选游戏连接的流量。" : "已收到所选游戏连接流量；尚不代表已识别本机角色。"));
                    last = timer.Elapsed.TotalSeconds;
                }
                token.WaitHandle.WaitOne(15);
            }
            progress.Report(new(count, timer.Elapsed.TotalSeconds, token.IsCancellationRequested ? "校对已停止。" : "30 秒校对完成。"));
        }
        finally { pcap_close(handle); }
    }, token);
    [StructLayout(LayoutKind.Sequential)] private struct Device { public nint Next, Name, Description, Addresses; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Address { public nint Next, Sockaddr, Netmask, Broadcast, Destination; }
    [StructLayout(LayoutKind.Sequential)] private struct Bpf { public uint Length; public nint Instructions; }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("iphlpapi")] private static extern uint GetExtendedTcpTable(nint data, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint table, uint reserved);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_findalldevs(out nint head, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_freealldevs(nint head);
    private static nint OpenConfigured(string name, StringBuilder error) => NativeCaptureBoundary.OpenConfigured(
        () => pcap_create(name, error), pcap_set_snaplen, pcap_set_buffer_size, pcap_set_promisc,
        pcap_set_timeout, pcap_activate, pcap_close);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint pcap_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_set_snaplen(nint handle, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_set_buffer_size(nint handle, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_set_promisc(nint handle, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_set_timeout(nint handle, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_activate(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_compile(nint handle, out Bpf filter, string expression, int optimize, uint netmask);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_setfilter(nint handle, ref Bpf filter);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_freecode(ref Bpf filter);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_setnonblock(nint handle, int value, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_next_ex(nint handle, out nint header, out nint packet);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_close(nint handle);
}

