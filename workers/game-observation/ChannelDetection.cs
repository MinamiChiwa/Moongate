using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation;

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
/// 网卡判定：累计每个候选网卡（含 Npcap 回环适配器）上目的端口属于游戏进程的下行包数、负载字节，以及负载能按游戏帧头分帧的包数
/// （<see cref="MabiTool.Providers.TwObservation.GameFrameSignature"/>），不保存包内容。任一网卡分出帧时按分帧包数判定：
/// 领先至少 <see cref="MinimumFrames"/> 个且不少于第二名的 <see cref="Margin"/> 倍；都分不出帧时按带负载的包数判定：
/// 领先至少 <see cref="MinimumPackets"/> 个且不少于第二名的 <see cref="Margin"/> 倍。加密隧道与加速器对外流量只有包没有帧，不会胜过能分帧的那张。
/// </summary>
public sealed class ChannelTrafficDetector(IReadOnlyList<(string Id, string Description)> channels)
{
    public const int MinimumPackets = 20;
    public const int MinimumFrames = 3;
    public const int EarlyPackets = 200;
    public const int Margin = 3;
    public const long MaximumPackets = 200_000;
    public const int MinimumDurationMs = 5_000;
    public const int MaximumDurationMs = 60_000;
    public const int MaximumChannels = 16;
    private readonly long[] packets = new long[channels.Count];
    private readonly long[] bytes = new long[channels.Count];
    private readonly long[] frames = new long[channels.Count];
    private readonly bool[] opened = new bool[channels.Count];
    private readonly string?[] issues = new string?[channels.Count];
    public long TotalPackets { get; private set; }
    public bool BudgetExhausted => TotalPackets >= MaximumPackets;
    public void Opened(int index) => opened[index] = true;
    public void Issue(int index, string issue) => issues[index] = issue.Length > 128 ? issue[..128] : issue;
    /// <summary>记录一个已通过下行过滤的包；负载为零的纯 ACK 只计包数预算。</summary>
    public void Record(int index, int payload, bool framed = false)
    {
        if (BudgetExhausted) return;
        TotalPackets++;
        if (payload <= 0) return;
        packets[index]++; bytes[index] += payload;
        if (framed) frames[index]++;
    }
    /// <summary>某张网卡的分帧包数（诊断与测试用）。</summary>
    public long Frames(int index) => frames[index];
    public (string? Winner, string? Reason) Decide(bool final)
    {
        var framed = frames.Any(value => value > 0);
        var counts = framed ? frames : packets;
        var order = Enumerable.Range(0, channels.Count).OrderByDescending(index => counts[index]).ToArray();
        if (order.Length == 0) return (null, final ? "observation.channelUndetermined" : null);
        var leader = counts[order[0]]; var second = order.Length > 1 ? counts[order[1]] : 0;
        var threshold = framed ? MinimumFrames : final ? MinimumPackets : EarlyPackets;
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

internal static partial class CaptureChannels
{
    private const int DetectionBufferBytes = 1024 * 1024;
    /// <summary>游戏进程拥有的连接（SYN_SENT–CLOSE_WAIT）的本地端口集合；检测与常驻捕获的过滤都只用它。</summary>
    internal static HashSet<ushort> GamePorts(int processId) =>
        Table().Where(row => row.ProcessId == processId && row.State is >= 3 and <= 8).Select(row => row.Connection.LocalPort).ToHashSet();

    /// <summary>
    /// 全部设备同时短时按游戏进程本地端口监听下行包（规则同常驻捕获的过滤），每 500 ms 报告进度。预算：时长 5–60 秒、至多 16 张设备、
    /// 累计 200,000 个包；每个句柄 1 MiB 内核缓冲；结束、取消或失败时关闭全部句柄。负载按游戏帧头特征计分帧包数。
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
            var current = GamePorts(game.ProcessId);
            var filter = NativeCaptureBoundary.Filter(current, []);
            for (var index = 0; index < candidates.Length; index++)
            {
                var error = new StringBuilder(256);
                var handle = NativeCaptureBoundary.OpenConfigured(() => pcap_create(candidates[index].Name, error), pcap_set_snaplen, pcap_set_buffer_size,
                    pcap_set_promisc, pcap_set_timeout, pcap_activate, pcap_close, bufferBytes: DetectionBufferBytes, required: false);
                if (handle == 0) { detector.Issue(index, "open"); continue; }
                handles[index] = handle;
                links[index] = pcap_datalink(handle);
                if (!GamePacket.SupportsLink(links[index])) { detector.Issue(index, "link:" + links[index].ToString(System.Globalization.CultureInfo.InvariantCulture)); Close(handles, index); continue; }
                if (!Apply(handle, filter) || pcap_setnonblock(handle, 1, error) != 0) { detector.Issue(index, "filter"); Close(handles, index); continue; }
                detector.Opened(index);
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
                        var length = Marshal.ReadInt32(header + 8);
                        if (!NativeCaptureBoundary.CompletePacket(length, Marshal.ReadInt32(header + 12))) { detector.Record(index, 0); continue; }
                        var raw = new byte[length]; Marshal.Copy(pointer, raw, 0, length);
                        var packet = GamePacket.Read(links[index], raw);
                        if (packet is null || !current.Contains(packet.DestinationPort)) { detector.Record(index, 0); continue; }
                        detector.Record(index, packet.Payload.Length,
                            packet.Payload.Length > 0 && GameFrameSignature.Matches(packet.Payload, GameFrameSignature.RegisteredProtocols));
                    }
                }
                var now = timer.Elapsed.TotalMilliseconds;
                if (now - lastPoll >= 1000)
                {
                    if (!SelectedGameProcess.IsRunning(game)) throw new WorkerObservationException("input.bindingChanged");
                    var next = GamePorts(game.ProcessId);
                    if (!next.SetEquals(current))
                    {
                        current = next; filter = NativeCaptureBoundary.Filter(current, []);
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
