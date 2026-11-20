// 来源：MinamiChiwa/MabiSync，固定提交 2cb69b5ddec8de4fe9d20ec98bbb410ac015e671。
using System.Buffers.Binary;
using System.Net;

namespace MabiTool.Providers.TwObservation;

public sealed record GameTcpPacket(string SourceAddress, ushort SourcePort, string DestinationAddress, ushort DestinationPort,
    uint Sequence, byte Flags, byte[] Payload);

/// <summary>
/// 从一帧抓包数据读出 IPv4 或 IPv6 上的 TCP 段。链路类型：1 以太网（含 VLAN 标签）、0 DLT_NULL（Npcap 回环，族值 2 为 IPv4，
/// 23/24/28/30 为 IPv6）、12 与 101 原始 IP（按版本号区分，Wintun 等三层隧道）、228 DLT_IPV4、229 DLT_IPV6。分片包与其他链路返回空。
/// </summary>
public static class GamePacket
{
    public static bool SupportsLink(int link) => link is 0 or 1 or 12 or 101 or 228 or 229;

    public static GameTcpPacket? Read(int link, ReadOnlySpan<byte> raw)
    {
        var at = 0; int? version = null;
        if (link == 1)
        {
            if (raw.Length < 14) return null;
            at = 14; var ether = BinaryPrimitives.ReadUInt16BigEndian(raw[12..]);
            while (ether is 0x8100 or 0x88a8)
            { if (raw.Length - at < 4) return null; ether = BinaryPrimitives.ReadUInt16BigEndian(raw[(at + 2)..]); at += 4; }
            version = ether switch { 0x800 => 4, 0x86dd => 6, _ => 0 };
        }
        else if (link == 0)
        {
            if (raw.Length < 4) return null;
            at = 4; version = BinaryPrimitives.ReadUInt32LittleEndian(raw) switch { 2 => 4, 23 or 24 or 28 or 30 => 6, _ => 0 };
        }
        else if (link == 228) version = 4;
        else if (link == 229) version = 6;
        else if (link is not (12 or 101)) return null;
        if (raw.Length <= at) return null;
        version ??= raw[at] >> 4;
        return version switch { 4 => ReadIpv4(raw, at), 6 => ReadIpv6(raw, at), _ => null };
    }

    private static GameTcpPacket? ReadIpv4(ReadOnlySpan<byte> raw, int at)
    {
        if (raw.Length - at < 20 || raw[at] >> 4 != 4 || raw[at + 9] != 6) return null;
        var ipLength = (raw[at] & 15) * 4; var total = BinaryPrimitives.ReadUInt16BigEndian(raw[(at + 2)..]);
        if (ipLength < 20 || total < ipLength + 20 || total > raw.Length - at || (BinaryPrimitives.ReadUInt16BigEndian(raw[(at + 6)..]) & 0x3fff) != 0) return null;
        return Tcp(raw, Ipv4(raw.Slice(at + 12, 4)), Ipv4(raw.Slice(at + 16, 4)), at + ipLength, at + total);
    }

    /// <summary>IPv4 地址文本按地址值缓存（至多 4,096 个），同一地址返回同一字符串实例；文本与 IPAddress.ToString 相同。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> Ipv4Text = new();
    private static string Ipv4(ReadOnlySpan<byte> address)
    {
        var key = BinaryPrimitives.ReadUInt32BigEndian(address);
        if (Ipv4Text.TryGetValue(key, out var text)) return text;
        text = new IPAddress(address).ToString();
        if (Ipv4Text.Count < 4096) Ipv4Text.TryAdd(key, text);
        return text;
    }

    /// <summary>IPv6：固定头之后按逐跳、路由、目的选项、AH 扩展头跳到 TCP；分片头只接受未分片的原子分片。</summary>
    private static GameTcpPacket? ReadIpv6(ReadOnlySpan<byte> raw, int at)
    {
        if (raw.Length - at < 40 || raw[at] >> 4 != 6) return null;
        var payload = BinaryPrimitives.ReadUInt16BigEndian(raw[(at + 4)..]);
        var end = at + 40 + payload;
        if (payload == 0 || end > raw.Length) return null;
        int next = raw[at + 6], offset = at + 40;
        for (var hops = 0; next != 6; hops++)
        {
            if (hops >= 8 || end - offset < 8) return null;
            int length;
            switch (next)
            {
                case 0 or 43 or 60: length = (raw[offset + 1] + 1) * 8; break;
                case 44:
                    if ((BinaryPrimitives.ReadUInt16BigEndian(raw[(offset + 2)..]) & 0xfff9) != 0) return null;
                    length = 8; break;
                case 51: length = (raw[offset + 1] + 2) * 4; break;
                default: return null;
            }
            next = raw[offset]; offset += length;
        }
        return offset > end ? null
            : Tcp(raw, new IPAddress(raw.Slice(at + 8, 16)).ToString(), new IPAddress(raw.Slice(at + 24, 16)).ToString(), offset, end);
    }

    private static GameTcpPacket? Tcp(ReadOnlySpan<byte> raw, string source, string destination, int tcp, int end)
    {
        if (end - tcp < 20) return null;
        var tcpLength = (raw[tcp + 12] >> 4) * 4;
        if (tcpLength < 20 || tcpLength > end - tcp) return null;
        return new(source, BinaryPrimitives.ReadUInt16BigEndian(raw[tcp..]), destination, BinaryPrimitives.ReadUInt16BigEndian(raw[(tcp + 2)..]),
            BinaryPrimitives.ReadUInt32BigEndian(raw[(tcp + 4)..]), raw[tcp + 13], raw[(tcp + tcpLength)..end].ToArray());
    }
}
