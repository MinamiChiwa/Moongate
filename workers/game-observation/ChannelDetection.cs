using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using MabiTool.Contracts;

namespace MabiTool.Observation.Worker;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CaptureChannelQuery(int ProcessId, DateTimeOffset ProcessStartTime);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelDetectionRequest(int ProcessId, DateTimeOffset ProcessStartTime, int DurationMs);
public sealed record ChannelDetectionEntry(string ChannelId, string Description, bool Opened, long Packets, long PayloadBytes, string? Issue);
/// <summary>检测进度与结果；Final 为真时 WinnerChannelId/Reason 为最终判定。</summary>
public sealed record ChannelDetectionProgress(double ElapsedSeconds, IReadOnlyList<ChannelDetectionEntry> Channels,
    string? WinnerChannelId, string? Reason, bool Final);

/// <summary>
/// 手动调试的网卡判定：只累计每个候选网卡上与游戏数据连接五元组匹配的下行包数与负载字节，不保存包内容。
/// 领先网卡至少 <see cref="MinimumPackets"/> 个带负载的包且不少于第二名的 <see cref="Margin"/> 倍时判定。
/// </summary>
public sealed class ChannelTrafficDetector(IReadOnlyList<(string Id, string Description)> channels)
{
    public const int MinimumPackets = 20;
    public const int EarlyPackets = 200;
    public const int Margin = 3;
    public const long MaximumPackets = 200_000;
    public const int MinimumDurationMs = 5_000;
    public const int MaximumDurationMs = 60_000;
    public const int MaximumChannels = 16;
    private readonly long[] packets = new long[channels.Count];
    private readonly long[] bytes = new long[channels.Count];
    private readonly bool[] opened = new bool[channels.Count];
    private readonly string?[] issues = new string?[channels.Count];
    public long TotalPackets { get; private set; }
    public bool BudgetExhausted => TotalPackets >= MaximumPackets;
    public void Opened(int index) => opened[index] = true;
    public void Issue(int index, string issue) => issues[index] = issue.Length > 128 ? issue[..128] : issue;
    /// <summary>记录一个已通过下行过滤的包；负载为零的纯 ACK 只计包数预算。</summary>
    public void Record(int index, int payload)
    {
        if (BudgetExhausted) return;
        TotalPackets++;
        if (payload <= 0) return;
        packets[index]++; bytes[index] += payload;
    }
    public (string? Winner, string? Reason) Decide(bool final)
    {
        var order = Enumerable.Range(0, channels.Count).OrderByDescending(index => packets[index]).ToArray();
        if (order.Length == 0) return (null, final ? "observation.channelUndetermined" : null);
        var leader = packets[order[0]]; var second = order.Length > 1 ? packets[order[1]] : 0;
        var threshold = final ? MinimumPackets : EarlyPackets;
        if (leader >= threshold && leader >= second * Margin) return (channels[order[0]].Id, null);
        return (null, final ? "observation.channelUndetermined" : null);
    }
    public ChannelDetectionProgress Progress(double elapsedSeconds, bool final)
    {
        var (winner, reason) = Decide(final);
        return new(Math.Round(elapsedSeconds, 3), channels.Select((value, index) => new ChannelDetectionEntry(value.Id, value.Description,
            opened[index], packets[index], bytes[index], issues[index])).ToArray(), winner, reason, final);
    }
}

internal static class DetectionPacket
{
    /// <summary>按链路类型读取 IPv4/TCP 头，用 IP 总长度计算 TCP 负载字节，支持截断到头部的抓包。</summary>
    internal static int Payload(int link, ReadOnlySpan<byte> bytes)
    {
        var offset = link switch { 0 => 4, 1 => 14, 12 or 101 or 228 => 0, _ => -1 };
        if (offset < 0 || bytes.Length < offset + 20) return 0;
        if (link == 1 && BinaryPrimitives.ReadUInt16BigEndian(bytes[12..]) != 0x0800) return 0;
        var ip = bytes[offset..];
        if (ip[0] >> 4 != 4 || ip[9] != 6) return 0;
        var header = (ip[0] & 15) * 4; var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (header < 20 || ip.Length < header + 13) return 0;
        var tcp = (ip[header + 12] >> 4) * 4;
        return Math.Max(0, total - header - tcp);
    }
}

internal static partial class CaptureChannels
{
    private const int DetectionSnapshotBytes = 256;
    private const int DetectionBufferBytes = 1024 * 1024;
    internal static string DownlinkFilter(IEnumerable<CaptureConnection> connections) => "tcp and (" + string.Join(" or ", connections.Select(value =>
        $"(src host {value.RemoteAddress} and src port {value.RemotePort} and dst host {value.LocalAddress} and dst port {value.LocalPort})")) + ")";
    /// <summary>游戏数据连接：远端 1111；没有时取进程全部已建立连接。至多 32 条。</summary>
    internal static IReadOnlyList<CaptureConnection> DataConnections(IReadOnlyList<CaptureConnection> owned)
    {
        var data = owned.Where(value => value.RemotePort == 1111).ToArray();
        return (data.Length > 0 ? data : owned).Take(32).ToArray();
    }

    /// <summary>
    /// 候选网卡同时短时监听游戏连接的下行包，每 500 ms 报告进度。预算：时长 5–60 秒、至多 16 张网卡、
    /// 累计 200,000 个包；每个句柄 256 字节截取、1 MiB 内核缓冲；结束、取消或失败时关闭全部句柄。
    /// </summary>
    public static async Task<ChannelDetectionProgress> DetectAsync(SelectedGameProcess game, IReadOnlyList<CaptureDevice> devices,
        int durationMs, Func<ChannelDetectionProgress, Task> progress, CancellationToken token)
    {
        durationMs = Math.Clamp(durationMs, ChannelTrafficDetector.MinimumDurationMs, ChannelTrafficDetector.MaximumDurationMs);
        var candidates = devices.Take(ChannelTrafficDetector.MaximumChannels).ToArray();
        var detector = new ChannelTrafficDetector(candidates.Select(value => (NpcapObservationSource.ChannelId(value.Name), value.Description)).ToArray());
        var handles = new nint[candidates.Length]; var links = new int[candidates.Length];
        var timer = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<CaptureConnection> current = DataConnections(Connections(game.ProcessId));
            if (current.Count == 0) throw new WorkerObservationException("observation.channelNotFound");
            var filter = DownlinkFilter(current);
            for (var index = 0; index < candidates.Length; index++)
            {
                var error = new StringBuilder(256);
                var handle = NativeCaptureBoundary.OpenConfigured(() => pcap_create(candidates[index].Name, error), pcap_set_snaplen, pcap_set_buffer_size,
                    pcap_set_promisc, pcap_set_timeout, pcap_activate, pcap_close, DetectionSnapshotBytes, DetectionBufferBytes, required: false);
                if (handle == 0) { detector.Issue(index, "open"); continue; }
                handles[index] = handle;
                if (!Apply(handle, filter) || pcap_setnonblock(handle, 1, error) != 0) { detector.Issue(index, "filter"); Close(handles, index); continue; }
                links[index] = pcap_datalink(handle); detector.Opened(index);
            }
            var lastPoll = 0d; var lastReport = 0d;
            while (!token.IsCancellationRequested && timer.ElapsedMilliseconds < durationMs && !detector.BudgetExhausted)
            {
                for (var index = 0; index < handles.Length; index++)
                {
                    if (handles[index] == 0) continue;
                    for (var i = 0; i < 200 && !detector.BudgetExhausted; i++)
                    {
                        var result = pcap_next_ex(handles[index], out var header, out var pointer);
                        if (result == 0) break;
                        if (result != 1) { detector.Issue(index, "read"); Close(handles, index); break; }
                        var length = Math.Min(Marshal.ReadInt32(header + 8), DetectionSnapshotBytes);
                        var raw = new byte[length]; Marshal.Copy(pointer, raw, 0, length);
                        detector.Record(index, DetectionPacket.Payload(links[index], raw));
                    }
                }
                var now = timer.Elapsed.TotalMilliseconds;
                if (now - lastPoll >= 1000)
                {
                    if (!SelectedGameProcess.IsRunning(game)) throw new WorkerObservationException("input.bindingChanged");
                    var next = DataConnections(Connections(game.ProcessId));
                    if (next.Count > 0 && !next.SequenceEqual(current))
                    {
                        current = next; filter = DownlinkFilter(current);
                        for (var index = 0; index < handles.Length; index++)
                            if (handles[index] != 0 && !Apply(handles[index], filter)) { detector.Issue(index, "filter"); Close(handles, index); }
                    }
                    lastPoll = now;
                }
                if (now - lastReport >= 500)
                {
                    lastReport = now;
                    if (now >= 3000 && detector.Decide(false).Winner is not null) break;
                    await progress(detector.Progress(timer.Elapsed.TotalSeconds, false)).ConfigureAwait(false);
                }
                await Task.Delay(10, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return detector.Progress(timer.Elapsed.TotalSeconds, true);
        }
        finally { for (var index = 0; index < handles.Length; index++) Close(handles, index); }
    }
    private static bool Apply(nint handle, string expression)
    {
        if (pcap_compile(handle, out var compiled, expression, 1, uint.MaxValue) != 0) return false;
        try { return pcap_setfilter(handle, ref compiled) == 0; }
        finally { pcap_freecode(ref compiled); }
    }
    private static void Close(nint[] handles, int index)
    {
        if (handles[index] == 0) return;
        pcap_close(handles[index]); handles[index] = 0;
    }
}
