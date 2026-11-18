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
    /// <summary>
    /// 诊断用的设备类别，由回环标志与 Npcap 描述推断：loopback、wintun（WireGuard/UU 等用的三层隧道）、tap（TAP-Windows，OpenVPN 与部分加速器）、
    /// hyperv（Hyper-V 虚拟交换机）、virtual（其他虚拟、VPN、隧道与 WAN Miniport），其余为 physical。不参与抓包准入。
    /// </summary>
    public string Kind
    {
        get
        {
            if (IsLoopback || Description.Contains("loopback", StringComparison.OrdinalIgnoreCase)) return "loopback";
            if (Description.Contains("wintun", StringComparison.OrdinalIgnoreCase)) return "wintun";
            if (Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) || Description.StartsWith("TAP ", StringComparison.OrdinalIgnoreCase)) return "tap";
            if (Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) return "hyperv";
            string[] virtualMarks = ["virtual", "vpn", "tunnel", "wireguard", "vmware", "virtualbox", "miniport", "zerotier", "tailscale"];
            return virtualMarks.Any(mark => Description.Contains(mark, StringComparison.OrdinalIgnoreCase)) ? "virtual" : "physical";
        }
    }
}
public sealed record CaptureConnection(string LocalAddress, ushort LocalPort, string RemoteAddress, ushort RemotePort)
{
    public string Label => $"{LocalAddress}:{LocalPort} ↔ {RemoteAddress}:{RemotePort}";
    public string Filter => $"tcp and ((src host {LocalAddress} and src port {LocalPort} and dst host {RemoteAddress} and dst port {RemotePort}) or (src host {RemoteAddress} and src port {RemotePort} and dst host {LocalAddress} and dst port {LocalPort}))";
}

/// <summary>Optional, read-only Npcap diagnostics. Never saves raw packets or transmits traffic.</summary>
internal static partial class CaptureChannels
{
    private const string Library = "MabiTool.OptionalNpcap";
    static CaptureChannels()
    {
        NativeLibrary.SetDllImportResolver(typeof(CaptureChannels).Assembly, (name, _, _) => name == Library
            ? LoadLibraryEx(NpcapInstallation.LibraryPath, 0, 0x100 | 0x800) : 0);
    }
    private static int encoding;
    /// <summary>
    /// 首次枚举前以 <c>pcap_init(PCAP_CHAR_ENC_UTF_8)</c> 让 Npcap 的网卡描述与错误文本使用 UTF-8；旧版 Npcap 没有该函数或调用失败时，
    /// 字符串按系统 ANSI 代码页解码（网卡名 <c>\Device\NPF_{GUID}</c> 是 ASCII，两种方式相同）。
    /// </summary>
    private static bool Utf8
    {
        get
        {
            if (Volatile.Read(ref encoding) == 0)
            {
                int result;
                try { result = pcap_init(1, new StringBuilder(256)) == 0 ? 1 : 2; }
                catch (EntryPointNotFoundException) { result = 2; }
                Interlocked.CompareExchange(ref encoding, result, 0);
            }
            return Volatile.Read(ref encoding) == 1;
        }
    }
    private static string? NativeText(nint value) => Utf8 ? Marshal.PtrToStringUTF8(value) : Marshal.PtrToStringAnsi(value);
    /// <summary>
    /// 列出网卡。网卡查询、手动调试与捕获都先经过这里：系统目录下没有 Npcap 时以 observation.npcapMissing 结束，
    /// 与已安装但加载失败（observation.npcapUnavailable）区分。
    /// </summary>
    public static IReadOnlyList<CaptureDevice> Devices()
    {
        if (!File.Exists(NpcapInstallation.LibraryPath)) throw new WorkerObservationException("observation.npcapMissing");
        var error = new StringBuilder(256);
        _ = Utf8;
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
                    // sockaddr_in 的地址在 +4（4 字节），sockaddr_in6 在 +8（16 字节）；地址至多 32 个。
                    var (offset, length) = (row.Sockaddr == 0 ? 0 : Marshal.ReadInt16(row.Sockaddr)) switch { 2 => (4, 4), 23 => (8, 16), _ => (0, 0) };
                    if (length > 0 && addresses.Count < 32)
                    { var bytes = new byte[length]; Marshal.Copy(row.Sockaddr + offset, bytes, 0, length); addresses.Add(new IPAddress(bytes).ToString()); }
                    addr = row.Next;
                }
                devices.Add(new(NativeText(item.Name) ?? "", NativeText(item.Description) ?? "网卡", addresses.ToArray()) { IsLoopback = (item.Flags & 1) != 0 });
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

    /// <summary>IPv4 与 IPv6 TCP 连接表的全部行（四元组、所属进程 ID、状态），用于确定游戏端口集合与预缓冲四元组的归属。</summary>
    internal static IReadOnlyList<(CaptureConnection Connection, int ProcessId, int State)> Table() => [.. Table(2), .. Table(23)];

    /// <summary>
    /// 按地址族读取 <c>TCP_TABLE_OWNER_PID_ALL</c>。AF_INET 行为 MIB_TCPROW_OWNER_PID（24 字节：状态、本地地址、本地端口、远端地址、远端端口、PID）；
    /// AF_INET6 行为 MIB_TCP6ROW_OWNER_PID（56 字节：本地地址 16、作用域 4、本地端口 4、远端地址 16、作用域 4、远端端口 4、状态 4、PID 4）。
    /// 端口为网络字节序的低 16 位；地址不带作用域，与抓包读出的地址写法一致。
    /// </summary>
    private static List<(CaptureConnection, int, int)> Table(uint family)
    {
        var width = family == 2 ? 24 : 56;
        uint size = 0; var result = GetExtendedTcpTable(0, ref size, false, family, 5, 0);
        if (result is not (0 or 122)) throw new IOException("无法读取游戏连接列表：" + result);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var memory = Marshal.AllocHGlobal(checked((int)Math.Max(size, 4u)));
            try
            {
                result = GetExtendedTcpTable(memory, ref size, false, family, 5, 0);
                if (result == 122) continue;
                if (result != 0) throw new IOException("无法读取游戏连接列表：" + result);
                var count = Marshal.ReadInt32(memory);
                if (count < 0 || 4L + count * (long)width > size) throw new IOException("游戏连接列表长度异常。");
                var rows = new List<(CaptureConnection, int, int)>();
                for (var i = 0; i < count; i++)
                {
                    var at = memory + 4 + i * width;
                    string Address(int offset, int length) { var bytes = new byte[length]; Marshal.Copy(at + offset, bytes, 0, length); return new IPAddress(bytes).ToString(); }
                    ushort Port(int offset) => (ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(at + offset));
                    var (local, localPort, remote, remotePort, state, pid) = family == 2
                        ? (Address(4, 4), Port(8), Address(12, 4), Port(16), Marshal.ReadInt32(at), Marshal.ReadInt32(at + 20))
                        : (Address(0, 16), Port(20), Address(24, 16), Port(44), Marshal.ReadInt32(at + 48), Marshal.ReadInt32(at + 52));
                    if (localPort != 0 && remotePort != 0) rows.Add((new(local, localPort, remote, remotePort), pid, state));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new IOException("连接正在变化，请刷新后重试。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Device { public nint Next, Name, Description, Addresses; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Address { public nint Next, Sockaddr, Netmask, Broadcast, Destination; }
    [StructLayout(LayoutKind.Sequential)] private struct Bpf { public uint Length; public nint Instructions; }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("iphlpapi")] private static extern uint GetExtendedTcpTable(nint data, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint table, uint reserved);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_init(uint options, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_findalldevs(out nint head, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_freealldevs(nint head);
    private static nint OpenConfigured(string name, StringBuilder error, int bufferBytes = NativeCaptureBoundary.BufferBytes) => NativeCaptureBoundary.OpenConfigured(
        () => pcap_create(name, error), pcap_set_snaplen, pcap_set_buffer_size, pcap_set_promisc,
        pcap_set_timeout, pcap_activate, pcap_close, bufferBytes: bufferBytes);
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

