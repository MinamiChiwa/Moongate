using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MabiTool.ObservationWorkerProbe;

/// <summary>样本中的一条下行 TCP 段（抓包时刻为相对样本首包的毫秒）。</summary>
internal sealed record SamplePacket(double Ms, int Connection, int Link, uint Sequence, byte Flags, byte[] Payload);

/// <summary>样本连接：端口来自采集器的连接表记录（没有时按序号合成），连接表列出区间用于模拟每 500 ms 的读表。</summary>
internal sealed record SampleConnection(int Index, string Id, ushort LocalPort, ushort RemotePort, string LocalAddress, string RemoteAddress,
    double ListedFrom, double ListedTo, bool Noise);

/// <summary>
/// 一份实采样本（采集器的 packets.jsonl，可选 connections.json）。只读加载；不复制到仓库。帧在非托管内存中按 Npcap 包头（timeval、caplen、len）
/// 与链路帧（DLT_NULL 或以太网 + IPv4 + TCP）预先排好，回放时直接把指针交给捕获管线，与读取 Npcap 缓冲同一入口。
/// </summary>
internal sealed class Sample : IDisposable
{
    public required string Name { get; init; }
    public required IReadOnlyList<SamplePacket> Packets { get; init; }
    public required IReadOnlyList<SampleConnection> Connections { get; init; }
    public long PayloadBytes => Packets.Sum(packet => (long)packet.Payload.Length);
    public double DurationMs => Packets.Count == 0 ? 0 : Packets[^1].Ms;
    private nint frames, headers;
    private int[] offsets = [];

    public static Sample Load(string directory, bool noise)
    {
        var packets = new List<SamplePacket>();
        var connections = new Dictionary<string, SampleConnection>(StringComparer.Ordinal);
        var known = new Dictionary<string, (ushort Local, ushort Remote, bool Loopback, double From, double To)>(StringComparer.Ordinal);
        var table = Path.Combine(directory, "connections.json");
        if (File.Exists(table))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(table));
            foreach (var row in document.RootElement.GetProperty("connections").EnumerateArray())
            {
                double Number(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
                known[row.GetProperty("connectionId").GetString()!] = ((ushort)row.GetProperty("localPort").GetInt32(), (ushort)row.GetProperty("remotePort").GetInt32(),
                    row.TryGetProperty("remoteScope", out var scope) && scope.GetString() == "loopback", Number("firstSeenTableMs"), Number("lastSeenTableMs"));
            }
        }
        long? first = null;
        var lastPacket = new Dictionary<string, (double First, double Last)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Path.Combine(directory, "packets.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var id = row.GetProperty("connectionId").GetString()!;
            var micros = long.Parse(row.GetProperty("capturedAtUnixMicroseconds").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            first ??= micros;
            var ms = (micros - first.Value) / 1000d;
            if (!connections.TryGetValue(id, out var connection))
            {
                var index = connections.Count;
                var (local, remote, loopback) = known.TryGetValue(id, out var row2) ? (row2.Local, row2.Remote, row2.Loopback) : ((ushort)(40000 + index), (ushort)1111, true);
                connections[id] = connection = new(index, id, local, remote, "127.0.0.1", loopback ? "127.0.0.1" : "43.212.230.170", double.NaN, double.NaN, false);
            }
            lastPacket[id] = lastPacket.TryGetValue(id, out var span) ? (span.First, ms) : (ms, ms);
            packets.Add(new(ms, connection.Index, row.GetProperty("linktype").GetInt32(), row.GetProperty("tcpSequence").GetUInt32(),
                (byte)row.GetProperty("tcpFlags").GetInt32(), Convert.FromBase64String(row.GetProperty("payloadBase64").GetString()!)));
        }
        // 连接表列出区间：采集器记录的区间（相对其读表起点），否则按首包前 0 ms 至末包后 1 s。
        var list = connections.Values.OrderBy(value => value.Index).Select(value =>
        {
            var (from, to) = lastPacket[value.Id];
            if (known.TryGetValue(value.Id, out var row) && double.IsFinite(row.From) && double.IsFinite(row.To)) (from, to) = (Math.Min(from, row.From), Math.Max(to, row.To) + 1000);
            else to += 1000;
            return value with { ListedFrom = from, ListedTo = to };
        }).ToList();
        if (noise && packets.Count > 0)
        {
            // 游戏进程的一条 TLS 连接（远端 443），落在游戏端口集合内：每 20 ms 一个 1,400 字节的应用数据记录，内容为固定种子伪随机字节。
            var index = list.Count; var end = packets[^1].Ms;
            list.Add(new(index, "noise-tls", 55443, 443, "127.0.0.1", "18.166.193.141", 0, end + 1000, true));
            var random = new Random(20261119); uint sequence = 1_000_000;
            var noisy = new List<SamplePacket>();
            for (double at = 5; at < end; at += 20)
            {
                var payload = new byte[1400]; random.NextBytes(payload);
                payload[0] = 0x17; payload[1] = 3; payload[2] = 3; BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3), 1395);
                noisy.Add(new(at, index, 0, sequence, 0x18, payload)); sequence += 1400;
            }
            packets = packets.Concat(noisy).OrderBy(packet => packet.Ms).ToList();
        }
        return new Sample { Name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)), Packets = packets, Connections = list };
    }

    /// <summary>按链路类型构造原始帧并写入非托管内存；包头的抓包时刻为 <paramref name="utcOrigin"/> 加样本相对时刻。</summary>
    public void Materialize(DateTimeOffset utcOrigin)
    {
        Release();
        var raw = Packets.Select(packet => Frame(packet)).ToArray();
        offsets = new int[raw.Length + 1];
        for (var i = 0; i < raw.Length; i++) offsets[i + 1] = checked(offsets[i] + raw[i].Length);
        frames = Marshal.AllocHGlobal(Math.Max(1, offsets[^1]));
        headers = Marshal.AllocHGlobal(Math.Max(1, raw.Length * 16));
        var originMicros = utcOrigin.ToUnixTimeMilliseconds() * 1000;
        for (var i = 0; i < raw.Length; i++)
        {
            Marshal.Copy(raw[i], 0, frames + offsets[i], raw[i].Length);
            var micros = originMicros + (long)Math.Round(Packets[i].Ms * 1000);
            var header = headers + i * 16;
            Marshal.WriteInt32(header, (int)(micros / 1_000_000)); Marshal.WriteInt32(header + 4, (int)(micros % 1_000_000));
            Marshal.WriteInt32(header + 8, raw[i].Length); Marshal.WriteInt32(header + 12, raw[i].Length);
        }
    }
    public nint Header(int index) => headers + index * 16;
    public nint Data(int index) => frames + offsets[index];

    private byte[] Frame(SamplePacket packet)
    {
        var connection = Connections[packet.Connection];
        if (packet.Payload.Length > 65495) throw new InvalidDataException("样本段超过 IPv4 总长上限");
        var tcp = new byte[20 + packet.Payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, connection.RemotePort); BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), connection.LocalPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), packet.Sequence); tcp[12] = 0x50; tcp[13] = packet.Flags;
        packet.Payload.CopyTo(tcp, 20);
        var ip = new byte[20 + tcp.Length];
        ip[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length); ip[8] = 64; ip[9] = 6;
        IPAddress.Parse(connection.RemoteAddress).GetAddressBytes().CopyTo(ip, 12); IPAddress.Parse(connection.LocalAddress).GetAddressBytes().CopyTo(ip, 16);
        tcp.CopyTo(ip, 20);
        return packet.Link == 1 ? [.. new byte[12], 0x08, 0x00, .. ip] : [2, 0, 0, 0, .. ip];
    }
    private void Release()
    {
        if (frames != 0) Marshal.FreeHGlobal(frames);
        if (headers != 0) Marshal.FreeHGlobal(headers);
        frames = headers = 0;
    }
    public void Dispose() => Release();
}
